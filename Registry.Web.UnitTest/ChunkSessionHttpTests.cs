using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-UPL-04, §7.1 (chunked upload) through HTTP: a session is keyed by the caller and the file id; <c>GET obj/chunk</c>
/// lists the open uploads of the caller (file id, path, size, source file name, last-modified time, stored chunks);
/// <c>DELETE obj/chunk/{fileId}</c> removes the stored chunks; a session of another user is a 404 for GET and DELETE; a
/// resume goes on at the stored path; the same file sent again after its commit is not answered from the old receipt.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ChunkSessionHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ChunkSessionHttpTests : IDisposable
{
    private const string Owner = "chunksession-owner";
    private const string Writer = "chunksession-writer";
    private const string Org = "chunksession-org";
    private const string Ds = "chunksession-ds";

    // The file id of the Hub: the SHA-256 (hex) of the dataset, the path, the size and the last-modified time
    private const string FileId = "d0e2bb1c6a7ad0f95e12a1fbd0d8a5c0e1c61c1a8a49c1e0b0f8b7a3b2d4c5e6";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _folder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns([]);
        _factory.DdbWrapper
            .Setup(d => d.AddWithHints(It.IsAny<string>(), It.IsAny<IReadOnlyList<AddItem>>(), It.IsAny<bool>()))
            .Returns((string ddbPath, IReadOnlyList<AddItem> items, bool _) => new BatchAddResult
            {
                Entries = items.Select(i => new BatchAddedEntry
                {
                    Path = Path.GetRelativePath(ddbPath, i.Path).Replace('\\', '/'),
                    Hash = i.Hint?.Sha256 ?? string.Empty,
                    Size = i.Hint?.Size ?? 0,
                    Type = EntryType.Generic,
                    Status = "added"
                }).ToList()
            });
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string _, string[] paths) => paths
                .Select(p => new EntryBuildArtifacts { Path = p, Exists = true, Hash = "h", Type = EntryType.Generic })
                .ToArray());
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        var writer = await _factory.CreateUserAsync(Writer);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        context.OrganizationsUsers.Add(new OrganizationUser
        {
            Organization = org,
            OrganizationSlug = Org,
            UserId = writer.Id,
            Permissions = OrganizationPermissions.ReadWrite,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = owner.Id
        });
        await context.SaveChangesAsync();

        _folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    [SetUp]
    public void SetUp()
    {
        var chunks = Path.Combine(_folder, IDDB.UploadsFolderName, "chunks");
        if (Directory.Exists(chunks)) Directory.Delete(chunks, true);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task OpenUploads_AreListedWithTheirSourceNameLastModifiedAndStoredChunks()
    {
        (await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "orto/Lamb.tif", 0, 3, fileName: "Lamb-Hill.tif",
            lastModified: 1731000000123))).Status.ShouldBe(HttpStatusCode.OK);

        var (status, body) = await Send(HttpMethod.Get, "/obj/chunk", Owner);

        status.ShouldBe(HttpStatusCode.OK);
        var open = body!.AsArray();
        open.Count.ShouldBe(1);
        open[0]!["fileId"]!.GetValue<string>().ShouldBe(FileId);
        open[0]!["path"]!.GetValue<string>().ShouldBe("orto/Lamb.tif");
        open[0]!["totalFileSize"]!.GetValue<long>().ShouldBe(12);
        open[0]!["sourceFileName"]!.GetValue<string>().ShouldBe("Lamb-Hill.tif");
        open[0]!["lastModified"]!.GetValue<long>().ShouldBe(1731000000123);
        open[0]!["received"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray().ShouldBe([0]);
        open[0]!["missing"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray().ShouldBe([1, 2]);
    }

    [Test]
    public async Task OpenUploads_OfAnotherUser_AreNotListed()
    {
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "mine.bin", 0, 2));
        await Send(HttpMethod.Post, "/obj/chunk", Writer, Chunk("writer-upload-0001", "theirs.bin", 0, 2));

        var (_, mine) = await Send(HttpMethod.Get, "/obj/chunk", Owner);
        var (_, theirs) = await Send(HttpMethod.Get, "/obj/chunk", Writer);

        mine!.AsArray().Select(o => o!["path"]!.GetValue<string>()).ToArray().ShouldBe(["mine.bin"]);
        theirs!.AsArray().Select(o => o!["path"]!.GetValue<string>()).ToArray().ShouldBe(["theirs.bin"]);
    }

    [Test]
    public async Task GetAndDelete_OfAnotherUsersSession_Are404_AndTheSessionStays()
    {
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "mine.bin", 0, 2));

        (await Send(HttpMethod.Get, $"/obj/chunk/{FileId}", Writer)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/obj/chunk/{FileId}", Writer)).Status.ShouldBe(HttpStatusCode.NotFound);

        (await Send(HttpMethod.Get, $"/obj/chunk/{FileId}", Owner)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task TwoUsers_WithTheSameFileId_HaveTwoSessions()
    {
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "same.bin", 0, 2));
        await Send(HttpMethod.Post, "/obj/chunk", Writer, Chunk(FileId, "same.bin", 1, 2));

        var (_, owner) = await Send(HttpMethod.Get, $"/obj/chunk/{FileId}", Owner);
        var (_, writer) = await Send(HttpMethod.Get, $"/obj/chunk/{FileId}", Writer);

        owner!["received"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray().ShouldBe([0]);
        writer!["received"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray().ShouldBe([1]);
    }

    [Test]
    public async Task Delete_RemovesTheStoredChunks_OfThatUploadOnly()
    {
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "gone.bin", 0, 2));
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk("kept-upload-0001", "kept.bin", 0, 2));
        Directory.GetDirectories(Path.Combine(_folder, IDDB.UploadsFolderName, "chunks")).Length.ShouldBe(2);

        var (status, _) = await Send(HttpMethod.Delete, $"/obj/chunk/{FileId}", Owner);

        status.ShouldBe(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Get, $"/obj/chunk/{FileId}", Owner)).Status.ShouldBe(HttpStatusCode.NotFound);
        Directory.GetDirectories(Path.Combine(_folder, IDDB.UploadsFolderName, "chunks")).Length.ShouldBe(1);
        (await Send(HttpMethod.Get, "/obj/chunk", Owner)).Body!.AsArray()
            .Select(o => o!["fileId"]!.GetValue<string>()).ToArray().ShouldBe(["kept-upload-0001"]);
        // Again, or for an id that never existed: unknown
        (await Send(HttpMethod.Delete, $"/obj/chunk/{FileId}", Owner)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Delete_OfAnInvalidFileId_IsABadRequest()
    {
        (await Send(HttpMethod.Delete, "/obj/chunk/x", Owner)).Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Resume_GoesOnAtTheStoredPath_AndTheUploadCommitsThere()
    {
        // The first run of the upload went to a renamed path (Keep both); after a reload the same file id comes
        // with whatever path the client holds: the session's path stands
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "orto/Lamb (2).tif", 0, 2));

        var (resumed, _) = await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "orto/Lamb.tif", 1, 2));
        var (status, entry) = await Send(HttpMethod.Post, $"/obj/chunk/{FileId}/commit", Owner);

        resumed.ShouldBe(HttpStatusCode.OK);
        status.ShouldBe(HttpStatusCode.Created);
        entry!["path"]!.GetValue<string>().ShouldBe("orto/Lamb (2).tif");
        File.Exists(Path.Combine(_folder, "orto", "Lamb (2).tif")).ShouldBeTrue();
        File.Exists(Path.Combine(_folder, "orto", "Lamb.tif")).ShouldBeFalse();
    }

    [Test]
    public async Task TheSameFileSentAgainAfterItsCommit_IsStoredAgain_NotAnsweredFromTheOldReceipt()
    {
        const string id = "again-upload-0001";
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(id, "again.bin", 0, 1, overwrite: true));
        (await Send(HttpMethod.Post, $"/obj/chunk/{id}/commit", Owner)).Status.ShouldBe(HttpStatusCode.Created);
        File.Delete(Path.Combine(_folder, "again.bin"));

        // The same file again (the Hub gives it the same id): its chunks are stored and its commit adds the file
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(id, "again.bin", 0, 1, overwrite: true));
        (await Send(HttpMethod.Post, $"/obj/chunk/{id}/commit", Owner)).Status.ShouldBe(HttpStatusCode.Created);

        File.Exists(Path.Combine(_folder, "again.bin")).ShouldBeTrue();
    }

    [Test]
    public async Task ALostCommitAnswer_IsAskedForByTheFileId()
    {
        const string id = "receipt-upload-0001";
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(id, "receipt.bin", 0, 1));
        await Send(HttpMethod.Post, $"/obj/chunk/{id}/commit", Owner);

        var (status, receipt) = await Send(HttpMethod.Get, $"/obj/uploads/{id}", Owner);

        status.ShouldBe(HttpStatusCode.OK);
        receipt!["state"]!.GetValue<string>().ShouldBe("Done");
        (await Send(HttpMethod.Get, $"/obj/uploads/{id}", Writer)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // Cancel while a chunk of the same session is being stored: the chunk is refused as an unknown upload (not a 500) and
    // nothing of the session is left (no folder whose chunk markers a new start would count)
    [Test]
    public async Task Delete_DuringAChunkWrite_IsNotAServerError_AndLeavesNothing()
    {
        await Send(HttpMethod.Post, "/obj/chunk", Owner, Chunk(FileId, "cancelled.bin", 0, 3));
        // Out of order (chunk 1 is missing), so stored without the upload's lock; an in-order chunk holds the lock and
        // the DELETE waits for it
        var body = new GatedBody([5, 6, 7, 8]);
        var content = Chunk(FileId, "cancelled.bin", 2, 3, body: body);

        var write = Send(HttpMethod.Post, "/obj/chunk", Owner, content);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300); // the server is storing the chunk: it has the first byte and waits for the rest
        var (deleted, _) = await Send(HttpMethod.Delete, $"/obj/chunk/{FileId}", Owner);
        body.Release.SetResult();
        var (written, problem) = await write;

        deleted.ShouldBe(HttpStatusCode.NoContent);
        written.ShouldBe(HttpStatusCode.NotFound);
        problem!["status"]!.GetValue<int>().ShouldBe(404);
        (await Send(HttpMethod.Get, $"/obj/chunk/{FileId}", Owner)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, "/obj/chunk", Owner)).Body!.AsArray().Count.ShouldBe(0);
        var chunks = Path.Combine(_folder, IDDB.UploadsFolderName, "chunks");
        (Directory.Exists(chunks) ? Directory.GetFileSystemEntries(chunks) : []).ShouldBeEmpty();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string route, string user,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, $"/orgs/{Org}/ds/{Ds}{route}") { Content = content }.AsUser(user);
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var trimmed = body.TrimStart();
        return (response.StatusCode, trimmed.StartsWith('{') || trimmed.StartsWith('[') ? JsonNode.Parse(body) : null);
    }

    // A chunk of 4 bytes; the file is `total` chunks
    private static MultipartFormDataContent Chunk(string fileId, string path, int index, int total, bool overwrite = false,
        string? fileName = null, long? lastModified = null, Stream? body = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(fileId), "fileId" },
            { new StringContent(index.ToString(CultureInfo.InvariantCulture)), "chunkIndex" },
            { new StringContent(total.ToString(CultureInfo.InvariantCulture)), "totalChunks" },
            { new StringContent((4 * total).ToString(CultureInfo.InvariantCulture)), "totalFileSize" },
            { new StringContent("4"), "chunkSize" },
            { new StringContent(path), "path" }
        };
        if (overwrite)
            content.Add(new StringContent("true"), "overwrite");
        if (fileName != null)
            content.Add(new StringContent(fileName), "fileName");
        if (lastModified != null)
            content.Add(new StringContent(lastModified.Value.ToString(CultureInfo.InvariantCulture)), "lastModified");
        HttpContent chunk = body == null ? new ByteArrayContent([1, 2, 3, 4]) : new StreamContent(body, 1);
        chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(chunk, "chunk", "blob");
        return content;
    }

    // A chunk body that gives its first byte, then waits for Release before the rest
    private sealed class GatedBody(byte[] content) : Stream
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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

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
}
