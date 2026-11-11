using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Services.Managers;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-UPL-03, D-040, AC-023 through HTTP: an upload (<c>POST …/obj</c>, a bundle, the chunked upload) of a name that
/// exists in its folder is refused with 409, the "already exists" message, <c>conflictPath</c> and <c>noRetry</c>,
/// before a byte is stored and again at the commit, unless the form field <c>overwrite=true</c> comes before the
/// file; then the file replaces the entry, in the entry's letter case. A repeated upload id answers from its receipt.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UploadNameClashHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UploadNameClashHttpTests : IDisposable
{
    private const string Owner = "nameclash-owner";
    private const string Org = "nameclash-org";
    private const string Ds = "nameclash-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _folder = null!;
    private readonly List<EntrySummary> _indexed = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // The index of the dataset: what a test puts in _indexed, one folder at a time
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns((string _, EntryQuery q) => _indexed
                .Where(e => string.Equals(Parent(e.Path), q.Path ?? string.Empty, StringComparison.Ordinal)).ToList());
        // libddb indexes every file it is given
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
        // Plain files: nothing to build
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string _, string[] paths) => paths
                .Select(p => new EntryBuildArtifacts { Path = p, Exists = true, Hash = "h", Type = EntryType.Generic })
                .ToArray());
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        _folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    [SetUp]
    public void SetUp() => _indexed.Clear();

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Upload_OfANameThatExists_Is409WithConflictPath_AndKeepsTheFile()
    {
        Existing("single.txt", "old");

        var (status, body) = await SendAsync("/obj", File("single.txt", "new"));

        status.ShouldBe(HttpStatusCode.Conflict);
        body!["detail"]!.GetValue<string>().ShouldBe("A file or folder named 'single.txt' already exists in this folder");
        body["conflictPath"]!.GetValue<string>().ShouldBe("single.txt");
        body["noRetry"]!.GetValue<bool>().ShouldBeTrue();
        Read("single.txt").ShouldBe("old");
        NoTempFiles();
    }

    [Test]
    public async Task Upload_WithOverwrite_ReplacesTheFile()
    {
        Existing("replaced.txt", "old");

        var (status, body) = await SendAsync("/obj", File("replaced.txt", "new", overwrite: true));

        status.ShouldBe(HttpStatusCode.Created);
        body!["path"]!.GetValue<string>().ShouldBe("replaced.txt");
        Read("replaced.txt").ShouldBe("new");
    }

    [Test]
    public async Task Upload_OfANameThatDiffersOnlyInCase_ClashesAndOverwritesTheExistingCase()
    {
        Existing("Case.txt", "old", indexed: true);

        var (refused, problem) = await SendAsync("/obj", File("case.TXT", "new"));
        refused.ShouldBe(HttpStatusCode.Conflict);
        problem!["conflictPath"]!.GetValue<string>().ShouldBe("Case.txt");

        var (replaced, entry) = await SendAsync("/obj", File("case.TXT", "new", overwrite: true));
        replaced.ShouldBe(HttpStatusCode.Created);
        entry!["path"]!.GetValue<string>().ShouldBe("Case.txt");
        Read("Case.txt").ShouldBe("new");
    }

    [Test]
    public async Task Bundle_WithOneClashingPart_RefusesThatPartOnly()
    {
        Existing("bundle-a.txt", "old");

        var content = new MultipartFormDataContent();
        AddPart(content, "bundle-a.txt", "new");
        AddPart(content, "bundle-b.txt", "fresh");

        var (status, body) = await SendAsync("/obj", content);

        status.ShouldBe(HttpStatusCode.OK);
        var results = body!.AsArray();
        results.Count.ShouldBe(2);
        results[0]!["status"]!.GetValue<int>().ShouldBe(409);
        results[0]!["conflictPath"]!.GetValue<string>().ShouldBe("bundle-a.txt");
        results[0]!["error"]!.GetValue<string>().ShouldBe("A file or folder named 'bundle-a.txt' already exists in this folder");
        results[1]!["status"]!.GetValue<int>().ShouldBe(201);
        Read("bundle-a.txt").ShouldBe("old");
        Read("bundle-b.txt").ShouldBe("fresh");
    }

    // The flag belongs to the file part it precedes: it does not reach the next file of the bundle
    [Test]
    public async Task Bundle_OverwriteOfOnePart_DoesNotReachTheNext()
    {
        Existing("bundle-replaced.txt", "old A");
        Existing("bundle-kept.txt", "old B");

        var content = new MultipartFormDataContent();
        AddPart(content, "bundle-replaced.txt", "new A", overwrite: true);
        AddPart(content, "bundle-kept.txt", "new B");

        var (status, body) = await SendAsync("/obj", content);

        status.ShouldBe(HttpStatusCode.OK);
        var results = body!.AsArray();
        results[0]!["status"]!.GetValue<int>().ShouldBe(201);
        results[1]!["status"]!.GetValue<int>().ShouldBe(409);
        results[1]!["conflictPath"]!.GetValue<string>().ShouldBe("bundle-kept.txt");
        Read("bundle-replaced.txt").ShouldBe("new A");
        Read("bundle-kept.txt").ShouldBe("old B");
    }

    [Test]
    public async Task Chunk_FirstChunkOfANameThatExists_Is409_AndStoresNothing()
    {
        Existing("big-old.bin", "keep");

        var (status, body) = await SendAsync("/obj/chunk", Chunk("clash-first-0001", "big-old.bin"));

        status.ShouldBe(HttpStatusCode.Conflict);
        body!["conflictPath"]!.GetValue<string>().ShouldBe("big-old.bin");
        body["noRetry"]!.GetValue<bool>().ShouldBeTrue();
        Directory.Exists(Path.Combine(ChunkedUploadManager.ChunksRoot(Ddb()), "clash-first-0001")).ShouldBeFalse();
        Read("big-old.bin").ShouldBe("keep");
    }

    [Test]
    public async Task Chunk_ANameWrittenBeforeTheCommit_Is409AtTheCommit()
    {
        var (stored, _) = await SendAsync("/obj/chunk", Chunk("clash-late-0001", "late.bin"));
        stored.ShouldBe(HttpStatusCode.OK);

        Existing("late.bin", "other writer");

        var (status, body) = await SendAsync("/obj/chunk/clash-late-0001/commit", null);

        status.ShouldBe(HttpStatusCode.Conflict);
        body!["conflictPath"]!.GetValue<string>().ShouldBe("late.bin");
        body["noRetry"]!.GetValue<bool>().ShouldBeTrue();
        Read("late.bin").ShouldBe("other writer");
    }

    // The refused commit keeps the chunks: once the user chose Replace, the same commit with overwrite adds them
    [Test]
    public async Task Chunk_ARefusedCommit_IsCommittedWithOverwrite_WithoutSendingTheChunksAgain()
    {
        var (stored, _) = await SendAsync("/obj/chunk", Chunk("clash-recommit-0001", "recommit.bin"));
        stored.ShouldBe(HttpStatusCode.OK);
        Existing("recommit.bin", "other writer");

        var (refused, _) = await SendAsync("/obj/chunk/clash-recommit-0001/commit", new MultipartFormDataContent());
        refused.ShouldBe(HttpStatusCode.Conflict);

        var form = new MultipartFormDataContent { { new StringContent("true"), "overwrite" } };
        var (status, body) = await SendAsync("/obj/chunk/clash-recommit-0001/commit", form);

        status.ShouldBe(HttpStatusCode.Created);
        body!["path"]!.GetValue<string>().ShouldBe("recommit.bin");
        Read("recommit.bin").ShouldBe("abcd");
    }

    [Test]
    public async Task Chunk_WithOverwriteInTheFirstChunk_ReplacesAtTheCommit()
    {
        Existing("chunk-replaced.bin", "old");

        var (stored, _) = await SendAsync("/obj/chunk", Chunk("clash-over-0001", "chunk-replaced.bin", overwrite: true));
        stored.ShouldBe(HttpStatusCode.OK);
        var (status, body) = await SendAsync("/obj/chunk/clash-over-0001/commit", null);

        status.ShouldBe(HttpStatusCode.Created);
        body!["path"]!.GetValue<string>().ShouldBe("chunk-replaced.bin");
        Read("chunk-replaced.bin").ShouldBe("abcd");
    }

    [Test]
    public async Task Upload_RepeatedUploadId_AnswersFromItsReceiptBeforeTheNameRule()
    {
        var (first, _) = await SendAsync("/obj", File("receipt.txt", "once"), uploadId: "clash-receipt-0001");
        first.ShouldBe(HttpStatusCode.Created);

        // The name exists now (this upload wrote it): the same request is answered from its receipt, not refused
        var (again, body) = await SendAsync("/obj", File("receipt.txt", "once"), uploadId: "clash-receipt-0001");

        again.ShouldBe(HttpStatusCode.Created);
        body!["path"]!.GetValue<string>().ShouldBe("receipt.txt");
    }

    [Test]
    public async Task Commit_Repeated_AnswersFromItsReceiptBeforeTheNameRule()
    {
        await SendAsync("/obj/chunk", Chunk("clash-commit-0001", "committed.bin"));
        var (first, _) = await SendAsync("/obj/chunk/clash-commit-0001/commit", null);
        first.ShouldBe(HttpStatusCode.Created);

        var (again, body) = await SendAsync("/obj/chunk/clash-commit-0001/commit", null);

        again.ShouldBe(HttpStatusCode.Created);
        body!["path"]!.GetValue<string>().ShouldBe("committed.bin");
    }

    private IDDB Ddb()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var ds = context.Datasets.Single(d => d.Slug == Ds);
        return scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef);
    }

    private void Existing(string path, string text, bool indexed = false)
    {
        var local = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        System.IO.File.WriteAllText(local, text);
        if (indexed)
            _indexed.Add(new EntrySummary { Path = path, Hash = "h", Type = EntryType.Generic });
    }

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(_folder, path));

    private void NoTempFiles()
    {
        var uploads = Path.Combine(_folder, IDDB.UploadsFolderName);
        if (!Directory.Exists(uploads)) return;
        Directory.EnumerateFiles(uploads, "*" + Registry.Web.Utilities.DatasetUploads.TempFileExtension,
            SearchOption.TopDirectoryOnly).ShouldBeEmpty();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(string route, HttpContent? content,
        string? uploadId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}{route}") { Content = content }
            .AsUser(Owner);
        if (uploadId != null)
            request.Headers.Add("X-Upload-Id", uploadId);
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var trimmed = body.TrimStart();
        return (response.StatusCode, trimmed.StartsWith('{') || trimmed.StartsWith('[') ? JsonNode.Parse(body) : null);
    }

    private static MultipartFormDataContent File(string path, string text, bool overwrite = false)
    {
        var content = new MultipartFormDataContent();
        AddPart(content, path, text, overwrite);
        return content;
    }

    private static void AddPart(MultipartFormDataContent content, string path, string text, bool overwrite = false)
    {
        content.Add(new StringContent(path), "path");
        if (overwrite)
            content.Add(new StringContent("true"), "overwrite");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", Path.GetFileName(path));
    }

    // One chunk of 4 bytes ("abcd") that is the whole file
    private static MultipartFormDataContent Chunk(string fileId, string path, bool overwrite = false)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(fileId), "fileId" },
            { new StringContent("0"), "chunkIndex" },
            { new StringContent("1"), "totalChunks" },
            { new StringContent(4.ToString(CultureInfo.InvariantCulture)), "totalFileSize" },
            { new StringContent("4"), "chunkSize" },
            { new StringContent(path), "path" }
        };
        if (overwrite)
            content.Add(new StringContent("true"), "overwrite");
        var chunk = new ByteArrayContent("abcd"u8.ToArray());
        chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(chunk, "chunk", "blob");
        return content;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}
