using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-DEV-09 through HTTP: an upload over the storage quota answers 507 <c>application/problem+json</c> with a
/// <c>type</c>, a <c>title</c>, the message and <c>noRetry: true</c>: on <c>POST …/obj</c>, on the first
/// <c>POST …/obj/chunk</c> and on the commit of the chunks.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UploadQuotaHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UploadQuotaHttpTests : IDisposable
{
    private const string Owner = "quota-owner";
    private const string Org = "quota-org";
    private const string Ds = "quota-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _folder = null!;
    private string _ownerId = null!;
    private readonly SwitchableQuota _quota = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // A zero window: the ledger evaluates the limit on every upload, so a test can change it
        _factory = new RegistryWebFactory(
            new System.Collections.Generic.Dictionary<string, string> { ["AppSettings:Uploads:QuotaWindowSeconds"] = "0" },
            services =>
            {
                services.RemoveAll<IStorageQuota>();
                services.AddSingleton<IStorageQuota>(_quota);
            });
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // An empty index: the name rule of an upload (D-040) lists the target folder
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        _ownerId = owner.Id;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        _folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    [SetUp]
    public void SetUp() => _quota.Limit = null;

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(HttpResponseMessage Response, JsonObject Problem)> PostAsync(string url, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content }.AsUser(Owner);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response, body.TrimStart().StartsWith('{') ? JsonNode.Parse(body)!.AsObject() : []);
    }

    private static void ShouldBeQuotaProblem(HttpResponseMessage response, JsonObject problem)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.InsufficientStorage);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        problem["status"]!.GetValue<int>().ShouldBe(507);
        problem["title"]!.GetValue<string>().ShouldBe("Insufficient Storage");
        problem["type"]!.GetValue<string>().ShouldBe("https://www.rfc-editor.org/rfc/rfc4918#section-11.5");
        problem["detail"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    private static MultipartFormDataContent File(string path)
    {
        var content = new MultipartFormDataContent { { new StringContent(path), "path" } };
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", path);
        return content;
    }

    private static MultipartFormDataContent Chunk(string fileId, string path, int index, int total) =>
        Chunk(fileId, path, index, total, 4, 4 * total);

    private static MultipartFormDataContent Chunk(string fileId, string path, int index, int total, int chunkSize,
        long totalFileSize)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(fileId), "fileId" },
            { new StringContent(index.ToString(CultureInfo.InvariantCulture)), "chunkIndex" },
            { new StringContent(total.ToString(CultureInfo.InvariantCulture)), "totalChunks" },
            { new StringContent(totalFileSize.ToString(CultureInfo.InvariantCulture)), "totalFileSize" },
            { new StringContent(chunkSize.ToString(CultureInfo.InvariantCulture)), "chunkSize" },
            { new StringContent(path), "path" }
        };
        var chunk = new ByteArrayContent(new byte[chunkSize]);
        chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(chunk, "chunk", "blob");
        return content;
    }

    [Test]
    public async Task Upload_OverTheQuota_Is507ProblemDetails()
    {
        _quota.Limit = new UserStorageInfo { Total = 1, Used = 0 };

        var (response, problem) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj", File("over-quota.bin"));

        ShouldBeQuotaProblem(response, problem);
    }

    [Test]
    public async Task Chunk_OverTheQuota_Is507ProblemDetails()
    {
        _quota.Limit = new UserStorageInfo { Total = 1, Used = 0 };

        var (response, problem) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk",
            Chunk("quota-first-chunk-0001", "over-quota-chunked.bin", 0, 2));

        ShouldBeQuotaProblem(response, problem);
    }

    [Test]
    public async Task Commit_OverTheQuota_Is507ProblemDetails()
    {
        // Every chunk fits, then the quota shrinks: the commit decides on the size on disk
        foreach (var index in new[] { 0, 1 })
        {
            var (stored, _) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk",
                Chunk("quota-commit-0001", "over-quota-commit.bin", index, 2));
            stored.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        _quota.Limit = new UserStorageInfo { Total = 1, Used = 0 };
        var (response, problem) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk/quota-commit-0001/commit");

        ShouldBeQuotaProblem(response, problem);
    }

    // FR-UPL-04: an open chunked upload holds room for its whole file until it is committed or removed (7 days), so the
    // first chunk of another one must fit with the open ones: else many large open uploads pass the quota
    [Test]
    public async Task SecondOpenChunkedUpload_ThatDoesNotFitWithTheFirst_Is507ProblemDetails()
    {
        const long gib = 1024L * 1024 * 1024;
        const int mib = 1024 * 1024;
        var chunks = Path.Combine(_folder, IDDB.UploadsFolderName, "chunks");
        if (Directory.Exists(chunks)) Directory.Delete(chunks, true);
        // 10 GiB left
        _quota.Limit = new UserStorageInfo { Total = 20 * gib, Used = 10 * gib };

        try
        {
            // Two files of 9 GiB in chunks of 1 MiB (9216 chunks): each fits alone, not both
            var (first, _) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk",
                Chunk("quota-open-0001", "first-9gib.bin", 0, 9216, mib, 9 * gib));
            first.StatusCode.ShouldBe(HttpStatusCode.OK);

            var (response, problem) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk",
                Chunk("quota-open-0002", "second-9gib.bin", 0, 9216, mib, 9 * gib));

            ShouldBeQuotaProblem(response, problem);
            Directory.GetDirectories(chunks).Length.ShouldBe(1, "the refused upload stores nothing");
        }
        finally
        {
            if (Directory.Exists(chunks)) Directory.Delete(chunks, true);
        }
    }

    // Two first chunks of one file sent at once: the second one finds the session that the first one just started (here
    // started while the second one checks the name). That session is the upload being checked, not another open one: it
    // is not counted twice against the quota
    [Test]
    public async Task FirstChunk_RacingTheSameFilesFirstChunk_DoesNotCountItsOwnSession()
    {
        const long gib = 1024L * 1024 * 1024;
        const int mib = 1024 * 1024;
        const string fileId = "quota-race-0001";
        const string path = "racing-9gib.bin";
        var chunks = Path.Combine(_folder, IDDB.UploadsFolderName, "chunks");
        if (Directory.Exists(chunks)) Directory.Delete(chunks, true);
        // 10 GiB left: the 9 GiB file fits once, not twice
        _quota.Limit = new UserStorageInfo { Total = 20 * gib, Used = 10 * gib };
        var store = _factory.Services.GetRequiredService<Registry.Web.Services.Adapters.ChunkStore>();
        var started = false;
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns(() =>
            {
                if (!started)
                {
                    // The other request's first chunk starts the session now
                    started = true;
                    store.Open(chunks, Registry.Web.Services.Managers.ChunkedUploadManager.SessionKey(_ownerId, fileId),
                        new ChunkManifest(path, 9 * gib, 9216, mib, _ownerId, Overwrite: false, FileId: fileId));
                }

                return [];
            });

        try
        {
            var (response, _) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk",
                Chunk(fileId, path, 0, 9216, mib, 9 * gib));

            started.ShouldBeTrue("the session was started while the chunk was checked");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            Directory.GetDirectories(chunks).Length.ShouldBe(1, "one session for the file");
        }
        finally
        {
            _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
                .Returns([]);
            if (Directory.Exists(chunks)) Directory.Delete(chunks, true);
        }
    }

    // The quota of a first chunk counts the open uploads of every dataset of the organization: a dataset whose chunks
    // folder cannot be read (or is removed meanwhile) is skipped, not a server error for the caller
    [Test]
    public async Task FirstChunk_WithAnUnreadableChunksFolderInAnotherDataset_IsStored()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix file modes");
            return;
        }

        var other = Path.Combine(Path.GetDirectoryName(_folder)!, "unreadable-" + Guid.NewGuid().ToString("N"));
        var otherChunks = Directory.CreateDirectory(Path.Combine(other, IDDB.UploadsFolderName, "chunks")).FullName;
        Directory.CreateDirectory(Path.Combine(otherChunks, "someone-elses"));
        System.IO.File.SetUnixFileMode(otherChunks, UnixFileMode.None);
        try
        {
            try
            {
                Directory.GetDirectories(otherChunks);
                Assert.Ignore("The folder stays readable for this user (root)");
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: the folder cannot be read
            }

            var (response, _) = await PostAsync($"/orgs/{Org}/ds/{Ds}/obj/chunk",
                Chunk("quota-unreadable-0001", "beside-unreadable.bin", 0, 2));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(otherChunks, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(other, true);
        }
    }

    /// <summary>The storage limit of the current user: none (null) or the one a test sets.</summary>
    private sealed class SwitchableQuota : IStorageQuota
    {
        public UserStorageInfo? Limit { get; set; }

        public UserStorageInfo GetUserStorage(Registry.Web.Identity.Models.User user) => Limit ?? new UserStorageInfo();

        public Task<UserStorageInfo?> GetCurrentUserStorageLimit() => Task.FromResult(Limit);

        public Task CheckCurrentUserStorage(long size = 0) =>
            Limit is { Total: { } total } limit && limit.Used + size > total
                ? throw new Registry.Web.Exceptions.QuotaExceededException(limit.Used + size, total)
                : Task.CompletedTask;
    }
}
