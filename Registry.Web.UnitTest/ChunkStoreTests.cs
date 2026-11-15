using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
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

    // FR-UPL-03: an upload started before the overwrite field existed replaced; it still does after an update
    [Test]
    public async Task Manifest_WrittenWithoutOverwrite_ReadsAsOverwrite()
    {
        await new ChunkStore().OpenAsync(_root, "file-0001", _manifest with { Overwrite = false });
        var path = Path.Combine(_root, "file-0001", ChunkStore.ManifestFileName);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("overwrite").ShouldBeTrue();
        File.WriteAllText(path, json.ToJsonString());

        new ChunkStore().GetStatus(_root, "file-0001")!.Manifest.Overwrite.ShouldBeTrue();
    }

    [Test]
    public async Task ChunksInOrder_FromDifferentRequests_MakeTheFile_AndItsHash()
    {
        for (var i = 0; i < _manifest.TotalChunks; i++)
        {
            var store = new ChunkStore(); // one request each
            await store.OpenAsync(_root, "file-0001", _manifest);
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
        await store.OpenAsync(_root, "file-0001", _manifest);
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
        await store.OpenAsync(_root, "file-0001", _manifest);
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
        await store.OpenAsync(_root, "file-0001", _manifest);

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
        await before.OpenAsync(_root, "file-0001", _manifest);
        await before.WriteAsync(_root, "file-0001", 0, Chunk(0));
        await before.WriteAsync(_root, "file-0001", 1, Chunk(1));

        var after = new ChunkStore(); // the process restarted: no hash in memory
        after.GetStatus(_root, "file-0001")!.Missing.ShouldBe([2, 3]);
        await after.OpenAsync(_root, "file-0001", _manifest);
        await after.WriteAsync(_root, "file-0001", 2, Chunk(2));
        await after.WriteAsync(_root, "file-0001", 3, Chunk(3));

        (await Take(after)).Sha256.ShouldBe(Sha(_file));
    }

    [Test]
    public async Task SameIdWithAnotherFile_IsAConflict_AndBadManifestsAreRefused()
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        await store.OpenAsync(_root, "file-0001", _manifest); // resuming is fine

        await Should.ThrowAsync<ConflictException>(() => store.OpenAsync(_root, "file-0001", _manifest with { Path = "other.bin" }));
        await Should.ThrowAsync<ConflictException>(() => store.OpenAsync(_root, "file-0001", _manifest with { UserId = "u2" }));
        await Should.ThrowAsync<BadRequestException>(() => store.OpenAsync(_root, "file-0002", _manifest with { TotalChunks = 5 }));
        await Should.ThrowAsync<BadRequestException>(() => store.OpenAsync(_root, "bad id", _manifest));
        await Should.ThrowAsync<BadRequestException>(() => store.OpenAsync(_root, "../escape0", _manifest));
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

        // §8.3: an upload folder goes 7 days after its last chunk, not before
        var longAgo = DateTime.UtcNow.AddDays(-8);
        File.SetLastWriteTimeUtc(Path.Combine(old, "data.bin"), longAgo);
        Directory.SetLastWriteTimeUtc(old, longAgo);
        // A session that the client can resume: its manifest keeps the file id
        var paused = Session(chunks, "paused-upload-1", "client-file-0001", DateTime.UtcNow.AddDays(-6));

        var removed = Registry.Web.Utilities.DatasetUploads.RemoveAbandoned(new Registry.Adapters.FileSystem(), dataset,
            DateTime.UtcNow, TimeSpan.FromHours(24));

        removed.ShouldBe(1);
        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(active).ShouldBeTrue();
        Directory.Exists(paused).ShouldBeTrue("six days after its last chunk a paused upload is still there");
    }

    // §8.3: 7 days only for a session with a file id; one from before the fix (no file id, no client can reach it under
    // the new keys) and a share batch's (no file id) go after the 24 hours of the other leftovers
    [Test]
    public void ChunkSweep_KeepsAResumableSessionSevenDays_AnyOtherOne24Hours()
    {
        var dataset = Path.Combine(_root, "dataset");
        var chunks = Path.Combine(dataset, ".uploads", "chunks");
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        var withoutFileId = Session(chunks, "legacy-upload-1", null, twoDaysAgo);
        var resumable = Session(chunks, "resumable-upload-1", "client-file-0001", twoDaysAgo);
        var expired = Session(chunks, "expired-upload-1", "client-file-0002", DateTime.UtcNow.AddDays(-8));

        var removed = Registry.Web.Utilities.DatasetUploads.RemoveAbandoned(new Registry.Adapters.FileSystem(), dataset,
            DateTime.UtcNow, TimeSpan.FromHours(24));

        removed.ShouldBe(2);
        Directory.Exists(withoutFileId).ShouldBeFalse("a session without a file id goes after 24 hours");
        Directory.Exists(resumable).ShouldBeTrue("a session with a file id stays 7 days");
        Directory.Exists(expired).ShouldBeFalse("a session with a file id goes after 7 days");
    }

    // A chunk session folder (manifest, data file, one marker) whose last write is `lastWrite`
    private string Session(string chunks, string name, string? fileId, DateTime lastWrite)
    {
        var folder = Directory.CreateDirectory(Path.Combine(chunks, name)).FullName;
        var received = Directory.CreateDirectory(Path.Combine(folder, ChunkStore.ReceivedFolderName)).FullName;
        File.WriteAllText(Path.Combine(folder, ChunkStore.ManifestFileName),
            Registry.Web.Utilities.ApiJson.Serialize(_manifest with { FileId = fileId }));
        File.WriteAllText(Path.Combine(folder, ChunkStore.DataFileName), "x");
        File.WriteAllText(Path.Combine(received, "0"), string.Empty);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, lastWrite);
        Directory.SetLastWriteTimeUtc(received, lastWrite);
        Directory.SetLastWriteTimeUtc(folder, lastWrite);
        return folder;
    }

    // A removal that was interrupted (or raced with a chunk) can leave a folder with chunk markers and no manifest: a new
    // start of the same id does not count those chunks as stored
    [Test]
    public async Task Open_OfAFolderWithoutManifest_DropsItsStaleChunkMarkers()
    {
        var received = Directory.CreateDirectory(Path.Combine(_root, "file-0001", ChunkStore.ReceivedFolderName)).FullName;
        File.WriteAllText(Path.Combine(received, "0"), string.Empty);
        File.WriteAllText(Path.Combine(received, "2"), string.Empty);

        var status = await new ChunkStore().OpenAsync(_root, "file-0001", _manifest);

        status.Received.ShouldBeEmpty();
        new ChunkStore().GetStatus(_root, "file-0001")!.Missing.ShouldBe([0, 1, 2, 3]);
    }

    // A chunk stored out of order is written without the upload's lock: a removal meanwhile makes it an unknown upload
    // (not an error of the server), and nothing of the upload is left
    [Test]
    public async Task Delete_WhileAChunkIsWritten_RefusesTheChunk_AndLeavesNothing()
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        var body = new GatedStream(_file.AsSpan((int)_manifest.OffsetOf(1), (int)ChunkSize).ToArray());

        var write = store.WriteAsync(_root, "file-0001", 1, body);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await store.DeleteAsync(_root, "file-0001")).ShouldBeTrue();
        body.Release.SetResult();

        await Should.ThrowAsync<NotFoundException>(() => write);
        Directory.Exists(Path.Combine(_root, "file-0001")).ShouldBeFalse();
        store.GetStatus(_root, "file-0001").ShouldBeNull();
    }

    // The first chunk is stored under the upload's lock (it is hashed as it streams): a removal waits for it, then
    // removes everything
    [Test]
    public async Task Delete_WhileTheFirstChunkIsWritten_WaitsForIt_AndLeavesNothing()
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        var body = new GatedStream(_file.AsSpan(0, (int)ChunkSize).ToArray());

        var write = store.WriteAsync(_root, "file-0001", 0, body);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The removal waits for the lock without blocking: it returns at once, not completed
        var delete = store.DeleteAsync(_root, "file-0001");
        delete.IsCompleted.ShouldBeFalse("the chunk being stored holds the upload");
        body.Release.SetResult();

        (await write).Received.ShouldBe([0]);
        (await delete).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "file-0001")).ShouldBeFalse();
        store.GetStatus(_root, "file-0001").ShouldBeNull();
    }

    // Two starts of one new upload at once (two of its chunks sent in parallel): it is admitted once, by the first, which
    // holds what its admission returned (a quota reservation) until the upload exists; the second resumes it
    [Test]
    public async Task Open_TwiceAtOnce_AdmitsTheUploadOnce_AndReleasesTheAdmissionOnceItExists()
    {
        var store = new ChunkStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissions = 0;
        var reservation = new Admission(() => File.Exists(Path.Combine(_root, "file-0001", ChunkStore.ManifestFileName)));

        async Task<IDisposable?> Admit()
        {
            Interlocked.Increment(ref admissions);
            admitted.TrySetResult();
            await gate.Task;
            return reservation;
        }

        var first = store.OpenAsync(_root, "file-0001", _manifest, Admit);
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = store.OpenAsync(_root, "file-0001", _manifest, Admit);
        second.IsCompleted.ShouldBeFalse("the first start holds the upload");
        gate.SetResult();

        (await first).Received.ShouldBeEmpty();
        (await second).Received.ShouldBeEmpty();
        admissions.ShouldBe(1);
        reservation.DisposedOnceTheUploadExisted.ShouldBe(true);
    }

    // An admission refused leaves nothing of the upload
    [Test]
    public async Task Open_RefusedByItsAdmission_StoresNothing()
    {
        var store = new ChunkStore();

        await Should.ThrowAsync<QuotaExceededException>(() => store.OpenAsync(_root, "file-0001", _manifest,
            () => throw QuotaExceededException.OwnerOverQuota(0)));

        store.GetStatus(_root, "file-0001").ShouldBeNull();
        Directory.Exists(Path.Combine(_root, "file-0001", ChunkStore.ReceivedFolderName)).ShouldBeFalse();
    }

    private sealed class Admission(Func<bool> uploadExists) : IDisposable
    {
        public bool? DisposedOnceTheUploadExisted { get; private set; }
        public void Dispose() => DisposedOnceTheUploadExisted = uploadExists();
    }

    // A start that must wait for the upload's lock (here held by the first chunk of an upload whose manifest is gone, as
    // after an interrupted removal) waits without holding a thread: the call returns at once, and the start completes
    // once the chunk lets go of the lock
    [Test]
    public async Task Open_WhileTheUploadIsLocked_WaitsWithoutBlocking()
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        var body = new GatedStream(_file.AsSpan(0, (int)ChunkSize).ToArray());
        var write = store.WriteAsync(_root, "file-0001", 0, body);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.Delete(Path.Combine(_root, "file-0001", ChunkStore.ManifestFileName));

        var open = store.OpenAsync(_root, "file-0001", _manifest);

        open.IsCompleted.ShouldBeFalse("the chunk being stored holds the upload");
        body.Release.SetResult();
        await Should.ThrowAsync<NotFoundException>(() => write);
        (await open).Received.ShouldBeEmpty();
        store.GetStatus(_root, "file-0001")!.Missing.ShouldBe([0, 1, 2, 3]);
    }

    // The cleanup does not wait for an upload that a request holds: one in use is not abandoned
    [Test]
    public async Task RemoveAbandoned_OfAnUploadInUse_KeepsItWithoutWaiting()
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        var body = new GatedStream(_file.AsSpan(0, (int)ChunkSize).ToArray());
        var write = store.WriteAsync(_root, "file-0001", 0, body);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // On another thread: a removal that waited for the lock fails the test instead of hanging it
        var removed = await Task.Run(() => store.RemoveAbandoned(Path.Combine(_root, "file-0001"), _ => true))
            .WaitAsync(TimeSpan.FromSeconds(10));

        removed.ShouldBeFalse();
        body.Release.SetResult();
        (await write).Received.ShouldBe([0]);
        store.RemoveAbandoned(Path.Combine(_root, "file-0001"), _ => true).ShouldBeTrue("once free it can go");
        store.UploadCount.ShouldBe(0);
    }

    // A body that gives its first byte, then waits for Release before the rest
    private sealed class GatedStream(byte[] content) : Stream
    {
        private int _position;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken ct = default)
        {
            if (_position == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            if (_position >= content.Length || buffer.Length == 0) return 0;
            var count = _position == 0 ? 1 : Math.Min(buffer.Length, content.Length - _position);
            content.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public void ChunkSweep_IsSevenDays_AConstant()
    {
        ChunkStore.AbandonedAfter.ShouldBe(TimeSpan.FromDays(7));
    }

    [Test]
    public async Task List_GivesTheOpenUploads_AndDeleteRemovesOne()
    {
        var store = new ChunkStore();
        var first = _manifest with { FileId = "client-file-0001", SourceFileName = "orto.tif", LastModified = 1700000000000 };
        await store.OpenAsync(_root, "aaaaaaaa", first);
        await store.WriteAsync(_root, "aaaaaaaa", 0, Chunk(0));
        await store.OpenAsync(_root, "bbbbbbbb", _manifest with { FileId = "client-file-0002" });

        var open = store.List(_root);

        open.Select(o => o.Manifest.FileId).Order().ToArray().ShouldBe(["client-file-0001", "client-file-0002"]);
        var listed = open.Single(o => o.Manifest.FileId == "client-file-0001");
        listed.Manifest.SourceFileName.ShouldBe("orto.tif");
        listed.Manifest.LastModified.ShouldBe(1700000000000);
        listed.Received.ShouldBe([0]);

        (await store.DeleteAsync(_root, "aaaaaaaa")).ShouldBeTrue();
        (await store.DeleteAsync(_root, "aaaaaaaa")).ShouldBeFalse("already gone");
        Directory.Exists(Path.Combine(_root, "aaaaaaaa")).ShouldBeFalse();
        store.List(_root).Select(o => o.Manifest.FileId).ToArray().ShouldBe(["client-file-0002"]);
        store.List(Path.Combine(_root, "nothing-here")).ShouldBeEmpty();
    }

    // A Cancel between a chunk's reading of the manifest and its taking of the upload's state: the chunk is refused as
    // an unknown upload and leaves no state in memory (nothing else would remove it)
    [TestCase(0)] // in order: hashed as it streams
    [TestCase(1)] // out of order: written outside the lock
    public async Task Write_AfterADeleteBetweenItsManifestAndItsState_LeavesNoState(int index)
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        store.AfterManifestRead = _ =>
        {
            store.AfterManifestRead = null;
            store.DeleteAsync(_root, "file-0001").GetAwaiter().GetResult().ShouldBeTrue();
        };

        await Should.ThrowAsync<NotFoundException>(() => store.WriteAsync(_root, "file-0001", index, Chunk(index)));

        store.UploadCount.ShouldBe(0);
        Directory.Exists(Path.Combine(_root, "file-0001")).ShouldBeFalse();
    }

    // The cleanup removes an abandoned upload through the store: its state in memory goes with its folder
    [Test]
    public async Task Sweep_OfAnUploadThatTookAChunk_LeavesNoState()
    {
        var store = new ChunkStore();
        var dataset = Path.Combine(_root, "dataset");
        var chunks = Path.Combine(dataset, ".uploads", "chunks");
        await store.OpenAsync(chunks, "file-0001", _manifest with { FileId = "client-file-0001" });
        await store.WriteAsync(chunks, "file-0001", 0, Chunk(0));
        store.UploadCount.ShouldBe(1);
        var folder = Path.Combine(chunks, "file-0001");
        var longAgo = DateTime.UtcNow.AddDays(-8);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, longAgo);
        Directory.SetLastWriteTimeUtc(Path.Combine(folder, ChunkStore.ReceivedFolderName), longAgo);
        Directory.SetLastWriteTimeUtc(folder, longAgo);

        var removed = Registry.Web.Utilities.DatasetUploads.RemoveAbandoned(new Registry.Adapters.FileSystem(), dataset,
            DateTime.UtcNow, TimeSpan.FromHours(24), chunks: store);

        removed.ShouldBe(1);
        Directory.Exists(folder).ShouldBeFalse();
        store.UploadCount.ShouldBe(0);
    }

    // A folder removed without the store (its dataset deleted, another instance's cleanup): its state is dropped by the
    // next new upload and by the cleanup run
    [Test]
    public async Task States_OfFoldersRemovedWithoutTheStore_AreDropped()
    {
        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        await store.WriteAsync(_root, "file-0001", 0, Chunk(0));
        Directory.Delete(Path.Combine(_root, "file-0001"), true);

        await store.OpenAsync(_root, "file-0002", _manifest);
        store.UploadCount.ShouldBe(1, "only the new upload has a state");

        Directory.Delete(Path.Combine(_root, "file-0002"), true);
        store.PruneRemoved().ShouldBe(1);
        store.UploadCount.ShouldBe(0);
    }

    // A chunks root that cannot be read (or was removed while listed) has no open upload to list: not a server error
    [Test]
    public async Task List_OfAnUnreadableRoot_IsEmpty()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix file modes");
            return;
        }

        var store = new ChunkStore();
        await store.OpenAsync(_root, "file-0001", _manifest);
        File.SetUnixFileMode(_root, UnixFileMode.None);
        try
        {
            try
            {
                Directory.GetDirectories(_root);
                Assert.Ignore("The folder stays readable for this user (root)");
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: the folder cannot be read
            }

            store.List(_root).ShouldBeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
