using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
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
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The single-purpose pieces of the UPL-01 upload pipeline and of REG-05/REG-07: hashing while
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
            var file = await DatasetUploads.StreamToTempAsync(new FileSystem(), root, new MemoryStream(content));

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
        var invalidated = new TaskCompletionSource();
        var keys = new Mock<IOgcCacheKeys>();
        keys.Setup(k => k.InvalidateAsync("org", "ds")).Callback(() => invalidated.TrySetResult()).Returns(Task.CompletedTask);
        var coalescer = new DatasetChangeCoalescer(keys.Object, Mock.Of<ICacheManager>(), [],
            TimeSpan.FromMilliseconds(50), NullLogger<DatasetChangeCoalescer>.Instance);

        coalescer.FilesAdded("org", "ds", ["a.tif"]);

        (await Task.WhenAny(invalidated.Task, Task.Delay(5000))).ShouldBe(invalidated.Task);
    }

    // ---- generation-scoped OGC keys (C.3)

    [Test]
    public async Task OgcCacheKeys_ChangeOnInvalidation_OnEveryProvider()
    {
        var keys = new OgcCacheKeys(new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        var before = await keys.ForAsync("org", "ds", "caps-wms-v2-1.3.0", "f");
        (await keys.ForAsync("org", "ds", "caps-wms-v2-1.3.0", "f")).ShouldBe(before);

        await keys.InvalidateAsync("org", "ds");

        (await keys.ForAsync("org", "ds", "caps-wms-v2-1.3.0", "f")).ShouldNotBe(before);
    }

    // ---- several file parts in one request (C.2)

    private static HttpRequest Multipart(params (string Name, string? FileName, string Content)[] parts)
    {
        const string boundary = "----upl01boundary";
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
            }, _ => "");

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
            }, temp => temp));

        temps.Count.ShouldBe(2);
        temps.ShouldAllBe(t => !File.Exists(t));
    }

    // ---- native work gate (REG-07 §5.2)

    [Test]
    public async Task NativeWorkGate_NeverRunsMoreThanTheLaneWidth()
    {
        var gate = new NativeWorkGate(new NativeConcurrencySettings { Tiles = 2, Heavy = 1 });
        var running = 0;
        var peak = 0;

        int Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            return now;
        }

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => gate.RunAsync(NativeLane.Tiles, Work)));
        peak.ShouldBe(2);

        peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => gate.RunAsync(NativeLane.Heavy, Work)));
        peak.ShouldBe(1);
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
        var gate = new SemaphoreSlim(0);
        var running = 0;
        var peak = 0;
        var done = 0;
        var generator = new Mock<IThumbnailGenerator>();
        generator.Setup(g => g.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()))
            .Returns(async () =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref running));
                await gate.WaitAsync();
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref done);
            });
        var cache = new Mock<ICacheManager>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns<string, string, object[]>(async (_, _, ps) => await ps.OfType<Func<Task<byte[]>>>().Single()());

        var queue = new ThumbnailPrecomputeQueue(cache.Object, generator.Object,
            new UploadSettings { ThumbnailPrecomputeWorkers = 2, ThumbnailPrecomputeCapacity = 3 },
            NullLogger<ThumbnailPrecomputeQueue>.Instance);

        var accepted = Enumerable.Range(0, 10)
            .Count(i => queue.TryEnqueue(new ThumbnailPrecomputeRequest("org", "ds", "h" + i, "/x" + i, 512)));

        await Task.Delay(200);
        peak.ShouldBe(2);
        accepted.ShouldBeInRange(3, 5); // 3 queued + up to 2 already taken by the workers
        gate.Release(10);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (done < accepted && DateTime.UtcNow < deadline) await Task.Delay(10);
        done.ShouldBe(accepted);
        await queue.StopAsync(CancellationToken.None);
    }

    // ---- dataset lookups (REG-05 item 3) and initialized folders (item 4)

    [Test]
    public void DatasetLookupCache_ServesCopies_AndForgetsAnOrganizationOnInvalidation()
    {
        var cache = new DatasetLookupCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var loads = 0;
        Data.Models.Dataset Load()
        {
            loads++;
            return new Data.Models.Dataset
            {
                Slug = "ds", InternalRef = Guid.NewGuid(),
                Organization = new Data.Models.Organization { Slug = "org", OwnerId = "u1" }
            };
        }

        var first = cache.GetOrLoad("org", "ds", Load)!;
        var second = cache.GetOrLoad("org", "ds", Load)!;
        loads.ShouldBe(1);
        second.ShouldNotBeSameAs(first);
        second.InternalRef.ShouldBe(first.InternalRef);
        second.Organization.OwnerId.ShouldBe("u1");

        // A change made to a returned copy never reaches the cache
        second.Organization.OwnerId = "u2";
        cache.GetOrLoad("org", "ds", Load)!.Organization.OwnerId.ShouldBe("u1");

        cache.InvalidateOrganization("org");
        cache.GetOrLoad("org", "ds", Load);
        loads.ShouldBe(2);

        // Misses are not cached
        cache.GetOrLoad("org", "missing", () => null).ShouldBeNull();
        cache.GetOrLoad("org", "missing", Load).ShouldNotBeNull();
    }

    [Test]
    public void DatasetLookupCache_InvalidationDuringALoad_IsNotCached()
    {
        var cache = new DatasetLookupCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var loads = 0;

        cache.GetOrLoad("org", "ds", () =>
        {
            loads++;
            cache.InvalidateDataset("org", "ds"); // the row changes while it is being read
            return new Data.Models.Dataset { Slug = "ds", Organization = new Data.Models.Organization { Slug = "org" } };
        });
        cache.GetOrLoad("org", "ds", () =>
        {
            loads++;
            return new Data.Models.Dataset { Slug = "ds", Organization = new Data.Models.Organization { Slug = "org" } };
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
