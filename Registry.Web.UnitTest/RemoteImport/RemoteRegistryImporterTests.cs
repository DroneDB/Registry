#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.RemoteImport;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.RemoteImport;

/// <summary>
/// <see cref="RemoteRegistryImporter"/> and its two strategies (the import of <c>SystemManager</c> as a
/// background job) over a fake source Registry: what is imported, what is reported, what stops the import.
/// </summary>
[TestFixture]
public sealed class RemoteRegistryImporterTests : IDisposable
{
    private const string Url = "http://remote.test";

    private string _root = null!;
    private RegistryContext _context = null!;
    private FakeRegistry _remote = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private readonly ConcurrentBag<string> _indexed = [];

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "remoteregistryimporter-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _context = TestDb.CreateContext<RegistryContext>();
        _remote = new FakeRegistry();
        _indexed.Clear();
        _ddb = new Mock<IDDB>();
        _ddb.Setup(d => d.AddRaw(It.IsAny<string>())).Callback<string>(_indexed.Add);
        _ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([]);
        _ddbManager = new Mock<IDdbManager>().WithFolders(Path.Combine(_root, "datasets"));
        _ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(_ddb.Object);
        _jobs = new Mock<IBackgroundJobsProcessor>();
        _jobs.Setup(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Returns("build-job");
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _remote.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    public void Dispose()
    {
        _context?.Dispose();
        _remote?.Dispose();
    }

    private Task<ImportResultDto> ImportAsync(RemoteImportRequest request, string? password = null,
        CancellationToken ct = default)
    {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_remote, false));
        // One retry: a file the source refuses fails after one backoff
        return TestImports.Importer(_context, _ddbManager.Object, http.Object, _root, TestJobs.Scheduler(_jobs.Object),
                new Registry.Web.Models.Configuration.ImportSettings { RegistryDownloadMaxRetries = 1 })
            .ImportAsync(request, password, ct);
    }

    private static RemoteImportRequest Dataset(ImportMode mode, string? destinationDs = null, string? user = null) =>
        new(Url, user, null, "src", "ds", "dst", destinationDs, mode);

    private string Folder(string org, string ds) =>
        Path.Combine(_root, "datasets", org, _context.Datasets.Single(d => d.Slug == ds && d.Organization.Slug == org)
            .InternalRef.ToString());

    private void VerifyBuildQueued(Times times) =>
        _jobs.Verify(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()), times);

    // Imports write slugs straight to the database: an invalid one is refused before the source is contacted
    [TestCase(ImportMode.Archive)]
    [TestCase(ImportMode.ParallelFiles)]
    public async Task AnInvalidDestinationSlug_IsRefused_BeforeAnyRequest(ImportMode mode)
    {
        var result = await ImportAsync(Dataset(mode, destinationDs: "jake--amy-property"));

        result.Errors.ShouldHaveSingleItem().Message.ShouldNotBeNull().ShouldContain("Invalid destination slug");
        _remote.Requests.ShouldBeEmpty();
        _context.Datasets.ShouldBeEmpty();
    }

    [Test]
    public async Task ParallelFiles_DownloadsAndIndexesEveryFile_ThenQueuesTheBuild()
    {
        _remote.Files["src/ds"] = ["a.jpg", "sub/b.jpg"];

        var result = await ImportAsync(Dataset(ImportMode.ParallelFiles));

        result.Errors.ShouldBeEmpty();
        result.ImportedItems.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            i => i.Organization.ShouldBe("dst"),
            i => i.Dataset.ShouldBe("ds"),
            i => i.FileCount.ShouldBe(2));
        result.TotalSize.ShouldBe(8);
        _indexed.OrderBy(p => p, StringComparer.Ordinal).ShouldBe(["a.jpg", "sub/b.jpg"]);
        File.ReadAllText(Path.Combine(Folder("dst", "ds"), "sub", "b.jpg")).ShouldBe("data");
        _context.Organizations.Single(o => o.Slug == "dst").IsPublic.ShouldBeFalse();
        VerifyBuildQueued(Times.Once());
    }

    [Test]
    public async Task ParallelFiles_AFileThatFails_IsReported_TheOthersAreImported()
    {
        _remote.Files["src/ds"] = ["a.jpg", "broken.jpg"];
        _remote.Broken.Add("broken.jpg");

        var result = await ImportAsync(Dataset(ImportMode.ParallelFiles));

        result.Errors.ShouldBeEmpty();
        result.FileErrors.ShouldHaveSingleItem().FilePath.ShouldBe("broken.jpg");
        _indexed.ShouldBe(["a.jpg"]);
        result.ImportedItems.ShouldHaveSingleItem().FileCount.ShouldBe(1);
    }

    [Test]
    public async Task ParallelFiles_RunAgain_SkipsTheFilesIndexedWithTheSourceHash()
    {
        _remote.Files["src/ds"] = ["a.jpg", "b.jpg"];
        await ImportAsync(Dataset(ImportMode.ParallelFiles));
        _remote.Requests.Clear();
        _indexed.Clear();
        _ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new EntrySummary { Path = "a.jpg", Hash = "h-a.jpg" }, new EntrySummary { Path = "b.jpg", Hash = "old" }]);

        var result = await ImportAsync(Dataset(ImportMode.ParallelFiles));

        _indexed.ShouldBe(["b.jpg"]);
        _remote.Requests.Count(r => r.Contains("/download", StringComparison.Ordinal)).ShouldBe(1);
        result.ImportedItems.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            i => i.FileCount.ShouldBe(2),
            i => i.SkippedFileCount.ShouldBe(1));
    }

    [Test]
    public async Task Archive_ReplacesTheDestinationContent_ThenQueuesTheBuild()
    {
        _remote.Archives["src/ds"] = ["a.jpg", ".ddb/dbase.sqlite"];
        await ImportAsync(Dataset(ImportMode.Archive));
        var folder = Folder("dst", "ds");
        File.WriteAllText(Path.Combine(folder, "stale.jpg"), "old");
        _remote.Archives["src/ds"] = ["new.jpg", ".ddb/dbase.sqlite"];

        var result = await ImportAsync(Dataset(ImportMode.Archive));

        result.Errors.ShouldBeEmpty();
        result.ImportedItems.ShouldHaveSingleItem().FileCount.ShouldBe(2);
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .ShouldBe([".ddb/dbase.sqlite", "new.jpg"]);
        VerifyBuildQueued(Times.Exactly(2));
        Directory.EnumerateFiles(_root, "import-*.zip").ShouldBeEmpty("the downloaded archive is removed");
    }

    [Test]
    public async Task Archive_ADownloadThatIsNotAZip_IsReported()
    {
        _remote.Archives["src/ds"] = null; // the remote answers an HTML page

        var result = await ImportAsync(Dataset(ImportMode.Archive));

        result.Errors.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Phase.ShouldBe(ImportPhase.Download),
            e => e.Message.ShouldNotBeNull().ShouldContain("valid ZIP"));
        _context.Datasets.ShouldBeEmpty();
    }

    [Test]
    public async Task Organization_ImportsEachDataset_UnderTheDestinationOrganization()
    {
        _remote.Datasets["src"] = ["one", "two"];
        _remote.Files["src/one"] = ["a.jpg"];
        _remote.Files["src/two"] = ["b.jpg"];

        var result = await ImportAsync(new RemoteImportRequest(Url, null, null, "src", null, "dst", null,
            ImportMode.ParallelFiles));

        result.ImportedItems.ShouldNotBeNull().Select(i => (i.Organization, i.Dataset)).Order()
            .ShouldBe([("dst", "one"), ("dst", "two")]);
    }

    [Test]
    public async Task ARefusedAuthentication_IsTheOnlyError()
    {
        var result = await ImportAsync(Dataset(ImportMode.ParallelFiles, user: "someone"), password: "wrong");

        result.Errors.ShouldHaveSingleItem().Phase.ShouldBe(ImportPhase.Authentication);
        _remote.Requests.ShouldBe([$"POST {Url}/users/authenticate"]);
    }

    [Test]
    public async Task Cancellation_StopsTheImport_ItIsNotReported()
    {
        _remote.Files["src/ds"] = ["a.jpg"];
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ImportAsync(Dataset(ImportMode.ParallelFiles), ct: cancelled.Token));
        _indexed.ShouldBeEmpty();
    }

    /// <summary>
    /// A source Registry: its organizations' datasets, the files of a dataset (search), each file (4 bytes, hash
    /// "h-" + path), the archive of a dataset; authentication is refused.
    /// </summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public Dictionary<string, string[]> Datasets { get; } = [];
        public Dictionary<string, string[]> Files { get; } = [];
        public Dictionary<string, string[]?> Archives { get; } = [];
        public HashSet<string> Broken { get; } = [];
        public ConcurrentQueue<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Enqueue($"{request.Method} {uri.GetLeftPart(UriPartial.Path)}");
            var segments = uri.AbsolutePath.Trim('/').Split('/');

            if (uri.AbsolutePath == "/users/authenticate")
                return Answer(HttpStatusCode.Unauthorized);

            // /orgs/{org}/ds
            if (segments is ["orgs", var org, "ds"])
                return Json(Datasets.GetValueOrDefault(org, []).Select(d => new { slug = d, name = d }));

            var dataset = segments.Length >= 4 ? $"{segments[1]}/{segments[3]}" : "";
            if (uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
                return Json(Files.GetValueOrDefault(dataset, []).Select(p =>
                    new { path = p, hash = "h-" + p, type = (int)EntryType.Image, size = 4 }));

            if (uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                var path = uri.Query.TrimStart('?').Split('&').Select(kv => kv.Split('=', 2))
                    .FirstOrDefault(kv => kv[0] == "path")?[1];
                if (path == null)
                    return Archive(Archives.GetValueOrDefault(dataset));

                path = Uri.UnescapeDataString(path);
                return Broken.Contains(path)
                    ? Answer(HttpStatusCode.NotFound)
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent("data"u8.ToArray())
                    });
            }

            return Answer(HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status) =>
            Task.FromResult(new HttpResponseMessage(status));

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });

        private static Task<HttpResponseMessage> Archive(string[]? entries)
        {
            if (entries == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html")
                });

            var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var entry in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                    writer.Write("data");
                }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zip.ToArray())
            });
        }
    }
}
