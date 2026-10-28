using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The single-purpose pieces of the upload pipeline: hashing while
/// streaming, the quota ledger, the change coalescer, multi-part reading, the native work gate,
/// the thumbnail pre-generation queue, the dataset lookup cache and the initialized-folder set.
/// </summary>
[TestFixture]
public class UploadPipelineComponentTests
{
    // ---- hashing while streaming (E.1)

    [Test]
    public async Task HashingWriteStream_DigestEqualsTheSha256OfTheContent()
    {
        var content = new byte[3 * 1024 * 1024 + 123];
        Random.Shared.NextBytes(content);
        using var target = new MemoryStream();

        await using (var hashing = new HashingWriteStream(target))
        {
            // Odd-sized writes, as a network body arrives
            for (var offset = 0; offset < content.Length; offset += 65_537)
                await hashing.WriteAsync(content.AsMemory(offset, Math.Min(65_537, content.Length - offset)));

            hashing.BytesWritten.ShouldBe(content.Length);
            hashing.GetHexDigest().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
        }
    }

    [Test]
    public async Task StreamToTempAsync_ReturnsTheHashAndTheWriteTimeOfTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "upl-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = Encoding.UTF8.GetBytes("hash me while you write me");
            var file = await DatasetUploads.StreamToTempAsync(TestScratch.Provider, new FileSystem(), root,
                new MemoryStream(content));

            file.Bytes.ShouldBe(content.Length);
            file.Hash.Size.ShouldBe(content.Length);
            file.Hash.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
            file.Hash.ModifiedUtc.ShouldBe(File.GetLastWriteTimeUtc(file.TempPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- quota (E.2, C.6)

    private static UserStorageInfo Limit(long used, long total) => new() { Used = used, Total = total };

    [Test]
    public async Task QuotaLedger_EvaluatesOncePerWindow_AndCountsEveryReservation()
    {
        var ledger = new UploadQuotaLedger(TimeSpan.FromMinutes(1));
        var evaluations = 0;
        Task<UserStorageInfo?> Evaluate() { Interlocked.Increment(ref evaluations); return Task.FromResult<UserStorageInfo?>(Limit(50, 100)); }

        var reservations = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => ledger.ReserveAsync("u1", 1, Evaluate)));

        evaluations.ShouldBe(1);
        // 50 used + 40 in flight: the next 11 bytes do not fit
        await Should.ThrowAsync<QuotaExceededException>(() => ledger.ReserveAsync("u1", 11, Evaluate));
        (await ledger.ReserveAsync("u1", 10, Evaluate)).Dispose();
        foreach (var r in reservations) r.Dispose();
    }

    [Test]
    public async Task QuotaLedger_StoredUploadsCount_ReleasedOnesDoNot()
    {
        var ledger = new UploadQuotaLedger(TimeSpan.FromMinutes(1));
        Task<UserStorageInfo?> Evaluate() => Task.FromResult<UserStorageInfo?>(Limit(0, 100));

        var stored = await ledger.ReserveAsync("u1", 60, Evaluate);
        stored.Commit();
        var abandoned = await ledger.ReserveAsync("u1", 30, Evaluate);
        abandoned.Dispose();

        await Should.ThrowAsync<QuotaExceededException>(() => ledger.ReserveAsync("u1", 41, Evaluate));
        (await ledger.ReserveAsync("u1", 40, Evaluate)).Dispose();
    }

    [Test]
    public async Task QuotaLedger_ResizeChecksTheGrowth_AndUnlimitedUsersAreNotLimited()
    {
        var ledger = new UploadQuotaLedger(TimeSpan.FromMinutes(1));
        var reservation = await ledger.ReserveAsync("u1", 10, () => Task.FromResult<UserStorageInfo?>(Limit(0, 100)));
        Should.Throw<QuotaExceededException>(() => reservation.Resize(101));
        reservation.Resize(100);
        reservation.Dispose();

        var admin = await ledger.ReserveAsync("admin", long.MaxValue / 2, () => Task.FromResult<UserStorageInfo?>(null));
        admin.Resize(long.MaxValue / 2 + 1);
        admin.Dispose();
    }

    [Test]
    public async Task QuotaLedger_EvaluatesAgainAfterTheWindow_KeepingWhatIsInFlight()
    {
        var ledger = new UploadQuotaLedger(TimeSpan.Zero);
        var used = 0L;
        Task<UserStorageInfo?> Evaluate() => Task.FromResult<UserStorageInfo?>(Limit(used, 100));

        var inFlight = await ledger.ReserveAsync("u1", 50, Evaluate);
        used = 40; // another upload was stored meanwhile and is now part of the evaluated use

        // 40 used + 50 still in flight
        await Should.ThrowAsync<QuotaExceededException>(() => ledger.ReserveAsync("u1", 11, Evaluate));
        inFlight.Dispose();
        (await ledger.ReserveAsync("u1", 60, Evaluate)).Dispose();
    }

    // ---- coalesced invalidation (C.3)

    [Test]
    public async Task ChangeCoalescer_InvalidatesOncePerDatasetPerWindow()
    {
        var keys = new Mock<IOgcCacheKeys>();
        var cache = new Mock<ICacheManager>();
        var coalescer = new DatasetChangeCoalescer(keys.Object, cache.Object, ["cover.jpg"],
            TimeSpan.FromMinutes(1), NullLogger<DatasetChangeCoalescer>.Instance);

        for (var i = 0; i < 40; i++)
            coalescer.FilesAdded("org", "ds", [$"f{i}.jpg"]);
        coalescer.FilesAdded("org", "other", ["cover.jpg"]);
        keys.Verify(k => k.InvalidateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        await coalescer.FlushAsync();

        keys.Verify(k => k.InvalidateAsync("org", "ds"), Times.Once);
        keys.Verify(k => k.InvalidateAsync("org", "other"), Times.Once);
        // Only the dataset that received a cover file drops its dataset thumbnail
        cache.Verify(c => c.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed,
            CacheCategories.ForDatasetThumbnail("org", "other")), Times.Once);
        cache.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        // A change after the flush opens the next window
        coalescer.FilesAdded("org", "ds", ["late.jpg"]);
        await coalescer.FlushAsync();
        keys.Verify(k => k.InvalidateAsync("org", "ds"), Times.Exactly(2));
    }

    [Test]
    public async Task ChangeCoalescer_FlushesByItselfAtTheEndOfTheWindow()
    {
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = new Mock<IOgcCacheKeys>();
        keys.Setup(k => k.InvalidateAsync("org", "ds")).Callback(() => invalidated.TrySetResult()).Returns(Task.CompletedTask);
        var time = new FakeTime(DateTime.UtcNow);
        var coalescer = new DatasetChangeCoalescer(keys.Object, Mock.Of<ICacheManager>(), [],
            TimeSpan.FromMilliseconds(50), NullLogger<DatasetChangeCoalescer>.Instance, time);

        coalescer.FilesAdded("org", "ds", ["a.tif"]);
        time.Advance(TimeSpan.FromMilliseconds(49));
        invalidated.Task.IsCompleted.ShouldBeFalse("the window is still open");

        time.Advance(TimeSpan.FromMilliseconds(1));
        await invalidated.Task;
        time.ArmedTimers.ShouldBe(0);
    }

    // ---- generation-scoped OGC keys (C.3)

    [Test]
    public async Task OgcCacheKeys_ChangeOnInvalidation_OnEveryProvider()
    {
        var keys = TestCaches.OgcKeys();

        var before = await keys.ForAsync("org", "ds", "caps-wms-v2-1.3.0", "f");
        (await keys.ForAsync("org", "ds", "caps-wms-v2-1.3.0", "f")).ShouldBe(before);

        await keys.InvalidateAsync("org", "ds");

        (await keys.ForAsync("org", "ds", "caps-wms-v2-1.3.0", "f")).ShouldNotBe(before);
    }

    // ---- several file parts in one request (C.2)

    private static HttpRequest Multipart(params (string Name, string? FileName, string Content)[] parts)
    {
        const string boundary = "----uploadboundary";
        var sb = new StringBuilder();
        foreach (var (name, fileName, content) in parts)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append('"');
            if (fileName != null) sb.Append("; filename=\"").Append(fileName).Append('"');
            sb.Append("\r\n\r\n").Append(content).Append("\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=" + boundary;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return context.Request;
    }

    [Test]
    public async Task ReadFilesFormAsync_PairsEachFileWithThePathSentBeforeIt()
    {
        var seen = new List<(string? Path, string Content)>();
        var form = await MultipartRequestHelper.ReadFilesFormAsync(
            Multipart(("path", null, "a/1.jpg"), ("file", "1.jpg", "one"), ("path", null, "a/2.jpg"),
                ("file", "2.jpg", "two"), ("file", "3.jpg", "three")),
            70, 8, async part =>
            {
                var content = await new StreamReader(part.Body).ReadToEndAsync();
                seen.Add((part.Path, content));
                return content;
            }, _ => { });

        seen.ShouldBe([("a/1.jpg", "one"), ("a/2.jpg", "two"), (null, "three")]);
        form.Files.Select(f => f.Path).ShouldBe(["a/1.jpg", "a/2.jpg", null]);
    }

    [Test]
    public async Task ReadFilesFormAsync_TooManyFiles_DeletesWhatWasWritten()
    {
        var temps = new List<string>();
        await Should.ThrowAsync<BadRequestException>(() => MultipartRequestHelper.ReadFilesFormAsync(
            Multipart(("file", "1.jpg", "one"), ("file", "2.jpg", "two"), ("file", "3.jpg", "three")),
            70, 2, async part =>
            {
                var temp = Path.GetTempFileName();
                await using (var f = File.Create(temp)) await part.Body.CopyToAsync(f);
                temps.Add(temp);
                return temp;
            }, temp => new FileSystem().TryDelete(temp)));

        temps.Count.ShouldBe(2);
        temps.ShouldAllBe(t => !File.Exists(t));
    }

    // ---- native work gate

    [Test]
    public async Task NativeWorkGate_NeverRunsMoreThanTheLaneWidth()
    {
        using var gate = new NativeWorkGate(new NativeConcurrencySettings { Tiles = 2, Heavy = 1 });

        (await PeakOf(gate, NativeLane.Tiles, width: 2, callers: 12)).ShouldBe(2);
        (await PeakOf(gate, NativeLane.Heavy, width: 1, callers: 6)).ShouldBe(1);
    }

    /// <summary>
    /// Runs <paramref name="callers"/> blocking works on <paramref name="lane"/>. Each holds its slot
    /// until the lane is full (<paramref name="width"/> works inside), so the lane is saturated
    /// while every other caller waits; then all are released. Returns the peak seen inside.
    /// </summary>
    private static async Task<int> PeakOf(NativeWorkGate gate, NativeLane lane, int width, int callers)
    {
        var running = 0;
        var peak = 0;
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();

        int Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            entered.Release();
            release.Wait();
            Interlocked.Decrement(ref running);
            return now;
        }

        var works = Enumerable.Range(0, callers).Select(_ => gate.RunAsync(lane, Work)).ToArray();
        for (var i = 0; i < width; i++)
            await entered.WaitAsync();
        gate.InFlight(lane).ShouldBe(width);
        Volatile.Read(ref running).ShouldBe(width);

        release.Set();
        await Task.WhenAll(works);
        return peak;
    }

    [Test]
    public async Task NativeWorkGate_CancelledWaiterLeavesTheQueue()
    {
        var gate = new NativeWorkGate(new NativeConcurrencySettings { Tiles = 1, Heavy = 1 });
        var release = new TaskCompletionSource<int>();
        var holder = gate.RunAsync(NativeLane.Heavy, () => release.Task);

        using var cts = new CancellationTokenSource();
        var waiter = gate.RunAsync(NativeLane.Heavy, () => 1, cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter);

        release.SetResult(0);
        await holder;
        gate.InFlight(NativeLane.Heavy).ShouldBe(0);
        (await gate.RunAsync(NativeLane.Heavy, () => 7)).ShouldBe(7);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do { current = target; } while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }

    // ---- bounded thumbnail pre-generation (C.4)

    [Test]
    public async Task ThumbnailPrecomputeQueue_RunsAtMostItsWorkers_AndRefusesWhenFull()
    {
        using var gate = new SemaphoreSlim(0);
        using var started = new SemaphoreSlim(0);
        using var finished = new SemaphoreSlim(0);
        var running = 0;
        var peak = 0;
        var done = 0;
        var generator = new Mock<IThumbnailGenerator>();
        generator.Setup(g => g.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref running));
                started.Release();
                await gate.WaitAsync();
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref done);
                finished.Release();
            });
        var cache = new Mock<ICacheManager>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .Returns<string, string, CancellationToken, object[]>(async (_, _, ct, ps) =>
                await ps.OfType<Func<CancellationToken, Task<byte[]>>>().Single()(ct));

        var queue = new ThumbnailPrecomputeQueue(cache.Object, generator.Object,
            new UploadSettings { ThumbnailPrecomputeWorkers = 2, ThumbnailPrecomputeCapacity = 3 },
            NullLogger<ThumbnailPrecomputeQueue>.Instance);

        var accepted = Enumerable.Range(0, 10)
            .Count(i => queue.TryEnqueue(new ThumbnailPrecomputeRequest("org", "ds", "h" + i, "/x" + i, 512)));

        // Both workers took a request and hold it: nothing else can start until the gate opens
        await started.WaitAsync();
        await started.WaitAsync();
        peak.ShouldBe(2);
        accepted.ShouldBeInRange(3, 5); // 3 queued + up to 2 already taken by the workers
        gate.Release(10);

        for (var i = 0; i < accepted; i++)
            await finished.WaitAsync();
        done.ShouldBe(accepted);
        peak.ShouldBe(2);
        await queue.StopAsync(CancellationToken.None);
    }

    // ---- dataset lookups and initialized folders

    [Test]
    public async Task DatasetLookupCache_ServesCopies_AndForgetsAnOrganizationOnInvalidation()
    {
        var cache = new DatasetLookupCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var loads = 0;
        Task<Data.Models.Dataset?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Data.Models.Dataset?>(new Data.Models.Dataset
            {
                Slug = "ds", InternalRef = Guid.NewGuid(),
                Organization = new Data.Models.Organization { Slug = "org", OwnerId = "u1" }
            });
        }

        var first = (await cache.GetOrLoadAsync("org", "ds", Load))!;
        var second = (await cache.GetOrLoadAsync("org", "ds", Load))!;
        loads.ShouldBe(1);
        second.ShouldNotBeSameAs(first);
        second.InternalRef.ShouldBe(first.InternalRef);
        second.Organization.OwnerId.ShouldBe("u1");

        // A change made to a returned copy never reaches the cache
        second.Organization.OwnerId = "u2";
        (await cache.GetOrLoadAsync("org", "ds", Load))!.Organization.OwnerId.ShouldBe("u1");

        cache.InvalidateOrganization("org");
        await cache.GetOrLoadAsync("org", "ds", Load);
        loads.ShouldBe(2);

        // Misses are not cached
        (await cache.GetOrLoadAsync("org", "missing", _ => Task.FromResult<Data.Models.Dataset?>(null))).ShouldBeNull();
        (await cache.GetOrLoadAsync("org", "missing", Load)).ShouldNotBeNull();
    }

    [Test]
    public async Task DatasetLookupCache_InvalidationDuringALoad_IsNotCached()
    {
        var cache = new DatasetLookupCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var loads = 0;

        await cache.GetOrLoadAsync("org", "ds", _ =>
        {
            loads++;
            cache.InvalidateOrganization("org"); // the row changes while it is being read
            return Task.FromResult<Data.Models.Dataset?>(new Data.Models.Dataset { Slug = "ds", Organization = new Data.Models.Organization { Slug = "org" } });
        });
        await cache.GetOrLoadAsync("org", "ds", _ =>
        {
            loads++;
            return Task.FromResult<Data.Models.Dataset?>(new Data.Models.Dataset { Slug = "ds", Organization = new Data.Models.Organization { Slug = "org" } });
        });

        loads.ShouldBe(2);
    }

    [Test]
    public void InitializedDatasetFolders_ForgetsAFolderAndEverythingBelowIt()
    {
        var folders = new InitializedDatasetFolders();
        var root = Path.Combine(Path.GetTempPath(), "datasets");
        folders.Add(Path.Combine(root, "org", "a"));
        folders.Add(Path.Combine(root, "org", "b") + Path.DirectorySeparatorChar);
        folders.Add(Path.Combine(root, "org2", "c"));

        folders.Forget(Path.Combine(root, "org"));

        folders.Contains(Path.Combine(root, "org", "a")).ShouldBeFalse();
        folders.Contains(Path.Combine(root, "org", "b")).ShouldBeFalse();
        folders.Contains(Path.Combine(root, "org2", "c")).ShouldBeTrue();
    }
}
