#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Import;
using Registry.Web.Services.RemoteImport;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Registry.Web.Exceptions;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The dataset walkers and remote importers classify reserved paths with the one shared rule,
/// <see cref="DatasetPathPolicy.IsReserved"/> (libddb's: a ".ddb" segment at any depth, ordinal;
/// ".uploads" only as the first segment). Each used its own rule before: the import walker and
/// both remote importers only skipped a root ".ddb/" prefix, so a nested ".ddb" file was indexed
/// or downloaded and the ".uploads" staging folder was indexed or written into.
/// </summary>
[TestFixture]
public class ReservedPathCallSitesTests
{
    // Remote listing: reserved paths (nested ".ddb", root ".uploads"), plus look-alikes that are
    // ordinary user files under libddb's ordinal, exact-segment rule.
    private static readonly string[] RemotePaths =
    [
        ".ddb/dbase.sqlite", "sub/.ddb/b.jpg", ".uploads/c.jpg", ".uploads/quarantine/d.jpg",
        "ok.jpg", ".DDB/e.jpg", ".ddbx/f.jpg", "photos/.uploads/g.jpg"
    ];

    private static readonly string[] ExpectedImported =
        [".DDB/e.jpg", ".ddbx/f.jpg", "ok.jpg", "photos/.uploads/g.jpg"];

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "reserved_sites_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void DatasetWalker_EnumerateFiles_SkipsReservedPathsOnly()
    {
        foreach (var rel in RemotePaths)
        {
            var local = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, "x");
        }

        // The one walker of ImportDatasetTool and IndexReconciliationService
        var files = TestPaths.Policy.EnumerateFiles(new FileSystem(), _root).OrderBy(p => p, StringComparer.Ordinal);

        files.ShouldBe(ExpectedImported);
    }

    [Test]
    public async Task RemoteRegistryClient_DownloadFilesParallelAsync_SkipsReservedPathsOnly()
    {
        var handler = new FakeRegistryHandler();

        await DownloadWithRemoteClientAsync(handler, RemotePaths);

        handler.Downloaded.OrderBy(p => p, StringComparer.Ordinal).ShouldBe(ExpectedImported);
    }

    /// <summary>
    /// A rooted or ".." path in the untrusted listing fails the whole download before any file is
    /// fetched, so nothing is written outside (or inside) the dataset folder.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoteRegistryClient_DownloadFilesParallelAsync_UnsafePath_FailsBeforeDownloading(bool absolute)
    {
        var handler = new FakeRegistryHandler();
        var unsafePath = UnsafePath(absolute);

        await Should.ThrowAsync<InvalidDatasetPathException>(async () =>
            await DownloadWithRemoteClientAsync(handler, ["ok.jpg", unsafePath]));

        handler.Downloaded.ShouldBeEmpty();
        EscapedFiles().ShouldBeEmpty();
    }

    [Test]
    public async Task RemoteImport_ParallelFiles_SkipsReservedPathsOnly()
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var handler = new FakeRegistryHandler(RemotePaths);
        var added = new ConcurrentBag<string>();

        var result = await ImportWithRemoteImporterAsync(context, handler, added);

        result.Errors.ShouldBeEmpty();
        result.FileErrors.ShouldBeEmpty();
        handler.Downloaded.OrderBy(p => p, StringComparer.Ordinal).ShouldBe(ExpectedImported);
        added.OrderBy(p => p, StringComparer.Ordinal).ShouldBe(ExpectedImported);
    }

    /// <summary>
    /// The legacy admin import used <c>Path.Combine(destPath, entry.Path)</c> on the untrusted
    /// listing without validation, so "../escape.txt" or a rooted path was downloaded outside the
    /// dataset folder. Like <see cref="RemoteRegistryClient"/>, an unsafe path now fails the import
    /// before any file is fetched.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoteImport_ParallelFiles_UnsafePath_FailsBeforeDownloading(bool absolute)
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var handler = new FakeRegistryHandler(["ok.jpg", UnsafePath(absolute)]);
        var added = new ConcurrentBag<string>();

        var result = await ImportWithRemoteImporterAsync(context, handler, added);

        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("Invalid path");
        handler.Downloaded.ShouldBeEmpty();
        added.ShouldBeEmpty();
        EscapedFiles().ShouldBeEmpty();
    }

    // The dataset folders sit two or more levels below _root (_root/dataset for the client,
    // _root/datasets/<org>/<ref> for the remote importer), so both unsafe paths target a file outside
    // the dataset folder but inside _root, which TearDown removes.
    private const string EscapeName = "escape.txt";

    private string UnsafePath(bool absolute) =>
        absolute ? Path.Combine(_root, EscapeName) : "../" + EscapeName;

    private string[] EscapedFiles() =>
        [.. Directory.EnumerateFiles(_root, EscapeName, SearchOption.AllDirectories)];

    private static HttpClient ClientFor(HttpMessageHandler handler) => new(handler, false);

    private async Task DownloadWithRemoteClientAsync(FakeRegistryHandler handler, string[] paths)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => ClientFor(handler));

        var client = new RemoteRegistryClient(factory.Object, new ImportSettings(),
            TestPaths.Policy,
            NullLogger<RemoteRegistryClient>.Instance);

        var entries = paths.Select(p => new EntryDto { Path = p, Type = EntryType.Image, Size = 4 }).ToArray();

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds",
            Path.Combine(_root, "dataset"), entries, null, CancellationToken.None);
    }

    private async Task<ImportResultDto> ImportWithRemoteImporterAsync(RegistryContext context,
        FakeRegistryHandler handler, ConcurrentBag<string> added)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => ClientFor(handler));

        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.AddRaw(It.IsAny<string>())).Callback<string>(added.Add);
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([]);
        var ddbManager = new Mock<IDdbManager>().WithFolders(Path.Combine(_root, "datasets"));
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);

        // The legacy admin import of SystemManager is the remote import job since RQ-12
        return await TestImports.Importer(context, ddbManager.Object, factory.Object, _root).ImportAsync(
            new RemoteImportRequest("http://remote.test", null, null, "src", "ds", "src", null, ImportMode.ParallelFiles),
            null, CancellationToken.None);
    }

    /// <summary>
    /// A remote Registry: POST .../search lists <c>listing</c>; GET .../download?path=... serves a
    /// 4-byte body and records the requested path.
    /// </summary>
    private sealed class FakeRegistryHandler(string[]? listing = null) : HttpMessageHandler
    {
        public ConcurrentBag<string> Downloaded { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            {
                var json = JsonSerializer.Serialize((listing ?? []).Select(p =>
                    new { path = p, hash = "h-" + p, type = (int)EntryType.Image, size = 4 }));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                var path = uri.Query.TrimStart('?').Split('&')
                    .Select(kv => kv.Split('=', 2))
                    .Single(kv => kv[0] == "path")[1];
                Downloaded.Add(Uri.UnescapeDataString(path));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("data"u8.ToArray())
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
