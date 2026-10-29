using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Resumable chunked uploads. Each test uses a new <see cref="ChunkStore"/> per request
/// where it matters: the share chunk endpoint used to keep its chunk tracker in a request-scoped
/// object, so an upload of more than one chunk never completed.
/// </summary>
[TestFixture]
public class ChunkStoreTests
{
    private const long ChunkSize = 1000;
    private string _root = null!;
    private byte[] _file = null!;
    private ChunkManifest _manifest = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "chunks-" + Guid.NewGuid().ToString("N"));
        _file = new byte[3 * ChunkSize + 333];
        Random.Shared.NextBytes(_file);
        _manifest = new ChunkManifest("big/file.bin", _file.Length, 4, ChunkSize, "u1");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private MemoryStream Chunk(int index) =>
        new MemoryStream(_file, (int)_manifest.OffsetOf(index), (int)_manifest.LengthOf(index));

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private async Task<(byte[] Content, string Sha256)> Take(ChunkStore store, string fileId = "file-0001")
    {
        var destination = Path.Combine(_root, "out.bin");
        var hint = await store.TakeAsync(_root, fileId, destination);
        hint.Size.ShouldBe(_file.Length);
        return (await File.ReadAllBytesAsync(destination), hint.Sha256);
    }

    [Test]
    public async Task ChunksInOrder_FromDifferentRequests_MakeTheFile_AndItsHash()
    {
        for (var i = 0; i < _manifest.TotalChunks; i++)
        {
            var store = new ChunkStore(); // one request each
            store.Open(_root, "file-0001", _manifest);
            var status = await store.WriteAsync(_root, "file-0001", i, Chunk(i));
            status.Complete.ShouldBe(i == _manifest.TotalChunks - 1);
        }

        var (content, sha) = await Take(new ChunkStore());
        content.ShouldBe(_file);
        sha.ShouldBe(Sha(_file));
        Directory.Exists(Path.Combine(_root, "file-0001")).ShouldBeFalse();
    }

    [Test]
    public async Task ChunksOutOfOrder_AndAgain_StillMakeTheFile()
    {
        var store = new ChunkStore();
        store.Open(_root, "file-0001", _manifest);
        foreach (var i in new[] { 2, 0, 3, 0, 1 })
            await store.WriteAsync(_root, "file-0001", i, Chunk(i));

        var (content, sha) = await Take(store);
        content.ShouldBe(_file);
        sha.ShouldBe(Sha(_file));
    }

    [Test]
    public async Task MissingChunk_IsReported_AndTakingIsAConflict()
    {
        var store = new ChunkStore();
        store.Open(_root, "file-0001", _manifest);
        await store.WriteAsync(_root, "file-0001", 0, Chunk(0));
        await store.WriteAsync(_root, "file-0001", 3, Chunk(3));

        var status = store.GetStatus(_root, "file-0001")!;
        status.Received.ShouldBe([0, 3]);
        status.Missing.ShouldBe([1, 2]);

        var ex = await Should.ThrowAsync<ConflictException>(() => store.TakeAsync(_root, "file-0001", Path.Combine(_root, "x")));
        ex.Message.ShouldContain("1,2");
    }

    [Test]
    public async Task ChunkOfTheWrongSize_IsRefused_AndNotRecorded()
    {
        var store = new ChunkStore();
        store.Open(_root, "file-0001", _manifest);

        await Should.ThrowAsync<BadRequestException>(() =>
            store.WriteAsync(_root, "file-0001", 1, new MemoryStream(new byte[ChunkSize - 1])));
        await Should.ThrowAsync<BadRequestException>(() =>
            store.WriteAsync(_root, "file-0001", 1, new MemoryStream(new byte[ChunkSize + 1])));

        store.GetStatus(_root, "file-0001")!.Received.ShouldBeEmpty();

        // The failed in-order attempt did not corrupt the hash: the upload still completes correctly
        for (var i = 0; i < _manifest.TotalChunks; i++)
            await store.WriteAsync(_root, "file-0001", i, Chunk(i));
        (await Take(store)).Sha256.ShouldBe(Sha(_file));
    }

    [Test]
    public async Task RestartInTheMiddle_RebuildsTheHashFromDisk()
    {
        var before = new ChunkStore();
        before.Open(_root, "file-0001", _manifest);
        await before.WriteAsync(_root, "file-0001", 0, Chunk(0));
        await before.WriteAsync(_root, "file-0001", 1, Chunk(1));

        var after = new ChunkStore(); // the process restarted: no hash in memory
        after.GetStatus(_root, "file-0001")!.Missing.ShouldBe([2, 3]);
        after.Open(_root, "file-0001", _manifest);
        await after.WriteAsync(_root, "file-0001", 2, Chunk(2));
        await after.WriteAsync(_root, "file-0001", 3, Chunk(3));

        (await Take(after)).Sha256.ShouldBe(Sha(_file));
    }

    [Test]
    public void SameIdWithAnotherFile_IsAConflict_AndBadManifestsAreRefused()
    {
        var store = new ChunkStore();
        store.Open(_root, "file-0001", _manifest);
        store.Open(_root, "file-0001", _manifest); // resuming is fine

        Should.Throw<ConflictException>(() => store.Open(_root, "file-0001", _manifest with { Path = "other.bin" }));
        Should.Throw<ConflictException>(() => store.Open(_root, "file-0001", _manifest with { UserId = "u2" }));
        Should.Throw<BadRequestException>(() => store.Open(_root, "file-0002", _manifest with { TotalChunks = 5 }));
        Should.Throw<BadRequestException>(() => store.Open(_root, "bad id", _manifest));
        Should.Throw<BadRequestException>(() => store.Open(_root, "../escape0", _manifest));
    }

    [Test]
    public async Task UnknownUpload_IsNotFound()
    {
        await Should.ThrowAsync<NotFoundException>(() =>
            new ChunkStore().WriteAsync(_root, "file-0009", 0, new MemoryStream([1])));
        new ChunkStore().GetStatus(_root, "file-0009").ShouldBeNull();
    }

    [Test]
    public void AbandonedChunkedUploads_AreSwept_ActiveOnesAreKept()
    {
        var dataset = Path.Combine(_root, "dataset");
        var chunks = Path.Combine(dataset, ".uploads", "chunks");
        var old = Directory.CreateDirectory(Path.Combine(chunks, "old-upload-01")).FullName;
        File.WriteAllText(Path.Combine(old, "data.bin"), "x");
        var active = Directory.CreateDirectory(Path.Combine(chunks, "new-upload-01")).FullName;
        File.WriteAllText(Path.Combine(active, "data.bin"), "x");

        var longAgo = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(Path.Combine(old, "data.bin"), longAgo);
        Directory.SetLastWriteTimeUtc(old, longAgo);

        var removed = Registry.Web.Utilities.DatasetUploads.RemoveAbandoned(new Registry.Adapters.FileSystem(), dataset,
            DateTime.UtcNow, TimeSpan.FromHours(24));

        removed.ShouldBe(1);
        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(active).ShouldBeTrue();
    }
}
