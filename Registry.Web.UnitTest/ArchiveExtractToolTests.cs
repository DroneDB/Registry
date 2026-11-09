using Registry.Web.Test.Support;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class ArchiveExtractToolTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ddb_extracttool_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    private static (ArchiveExtractTool tool, Mock<IStorageQuota> utils) CreateTool(
        long maxArchiveBytes = 5L * 1024 * 1024 * 1024, bool registerQuota = true)
    {
        var extractor = new SharpCompressArchiveExtractor();

        var utils = new Mock<IStorageQuota>();
        utils.Setup(u => u.CheckCurrentUserStorage(It.IsAny<long>())).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        // When registerQuota is false the scope has no IStorageQuota, reproducing the
        // processing-node container where the service is not registered.
        if (registerQuota)
            services.AddSingleton(utils.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var settings = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings { MaxArchiveExtractSizeBytes = maxArchiveBytes }
        };

        var tool = new ArchiveExtractTool(
            TestArchives.Extractor(extractor), scopeFactory, TestSettings.Options<ImportSettings>(Microsoft.Extensions.Options.Options.Create(settings)), TestSettings.Options<ProcessingPlatformSettings>(Microsoft.Extensions.Options.Options.Create(settings)),
            TestPaths.Policy,
            TestHeadroom.Default,
            TestJobs.FileIndexer());

        return (tool, utils);
    }

    private static IHeavyToolValidationContext Ctx(IDDB ddb, bool withCaller = true)
    {
        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb);
        // At submit time the caller is the authenticated principal; on the Hangfire
        // worker it is null. The quota guard only runs when the caller is present.
        ctx.SetupGet(c => c.Caller)
            .Returns(withCaller ? new ClaimsPrincipal(new ClaimsIdentity()) : null);
        return ctx.Object;
    }

    private static HeavyToolRequest Request(object prms)
    {
        var element = JsonSerializer.SerializeToElement(prms);
        return new HeavyToolRequest("archive-extract", "1", "org", "ds", null, element);
    }

    private Mock<IDDB> MockDdb(string archiveRelPath, string localArchivePath, EntryType type = EntryType.Generic, long size = 0)
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dir);
        ddb.Setup(d => d.GetEntry(archiveRelPath)).Returns(new Entry { Path = archiveRelPath, Type = type, Size = size });
        ddb.Setup(d => d.GetLocalPath(archiveRelPath)).Returns(localArchivePath);
        return ddb;
    }

    [Test]
    public async Task ValidateAsync_ValidZip_Passes()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, utils) = CreateTool();

        await tool.ValidateAsync(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object), CancellationToken.None);

        // User storage quota must be checked against the uncompressed size.
        utils.Verify(u => u.CheckCurrentUserStorage(It.Is<long>(v => v > 0)), Times.Once);
    }

    [Test]
    public async Task ValidateAsync_NoCaller_SkipsQuotaCheck()
    {
        // Reproduces the Hangfire worker (processing node) scenario: ctx.Caller is null
        // and IStorageQuota is NOT registered in the scope. ValidateAsync must neither resolve
        // IStorageQuota nor throw - the quota was already enforced at submit time.
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, utils) = CreateTool(registerQuota: false);

        await Should.NotThrowAsync(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip" }),
                Ctx(ddb.Object, withCaller: false), CancellationToken.None));

        // The storage quota guard must be skipped entirely on the worker.
        utils.Verify(u => u.CheckCurrentUserStorage(It.IsAny<long>()), Times.Never);
    }

    [Test]
    public void ValidateAsync_MissingSource_Throws()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dir);
        ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns((Entry)null!);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "missing.zip" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_UnsupportedFormat_Throws()
    {
        var local = Path.Combine(_dir, "photo.jpg");
        File.WriteAllText(local, "not an archive");
        var ddb = MockDdb("photo.jpg", local);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "photo.jpg" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_ReservedDestPath_Throws()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = ".ddb/evil" }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    // The reserved rule is libddb's, a ".ddb" segment at any depth (libddb would skip it).
    [TestCase("sub/.ddb")]
    [TestCase(@"sub\.ddb\evil")]
    public void ValidateAsync_NestedReservedDestPath_Throws(string destPath)
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    // ".ddbx" only starts like the index folder; it is an ordinary folder.
    [Test]
    public async Task ValidateAsync_DdbLookalikeDestPath_Passes()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        await tool.ValidateAsync(
            Request(new { sourcePath = "flight.zip", destPath = ".ddbx" }),
            Ctx(ddb.Object), CancellationToken.None);
    }

    [Test]
    public void ValidateAsync_DestPathTraversal_Throws()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = "../escape" }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ValidateAsync_DestPathIsFile_Is409WithItsPath_WhateverOverwriteSays(bool overwrite)
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        // The destination resolves to an existing non-folder entry.
        ddb.Setup(d => d.EntryExists("existing.txt")).Returns(true);
        ddb.Setup(d => d.GetEntry("existing.txt"))
            .Returns(new Entry { Path = "existing.txt", Type = EntryType.Generic });

        var (tool, _) = CreateTool();

        var ex = Should.Throw<NameClashException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = "existing.txt", overwrite }),
                Ctx(ddb.Object), CancellationToken.None));
        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldBe("A file or folder named 'existing.txt' already exists in this folder");
        ex.ConflictPath.ShouldBe("existing.txt");
    }

    [Test]
    public void ValidateAsync_DestPathIsAFileInAnotherLetterCase_Is409WithTheExistingPath()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q =>
            string.IsNullOrEmpty(q.Path) ? [new EntrySummary { Path = "Existing.TXT", Type = EntryType.Generic }] : []);

        var (tool, _) = CreateTool();

        Should.Throw<NameClashException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = "existing.txt" }),
                Ctx(ddb.Object), CancellationToken.None)).ConflictPath.ShouldBe("Existing.TXT");
    }

    [Test]
    public void Label_NamesTheArchiveAndTheFolder_AndNoOutputIsDeclared()
    {
        var (tool, _) = CreateTool();

        tool.Label(Request(new { sourcePath = "drops/flight.zip", destPath = "photos" }))
            .ShouldBe("drops/flight.zip \u2192 photos/");
        tool.Label(Request(new { sourcePath = "flight.zip" })).ShouldBe("flight.zip \u2192 /");
        tool.OutputPaths(Request(new { sourcePath = "flight.zip", destPath = "photos" })).ShouldBeEmpty();
    }

    [Test]
    public void ValidateAsync_ArchiveTooLarge_Throws()
    {
        var local = Path.Combine(_dir, "big.zip");
        CreateZip(local, "a.txt", "hello");
        // Report a huge archive size via the index entry.
        var ddb = MockDdb("big.zip", local, size: 10L * 1024 * 1024 * 1024);

        var (tool, _) = CreateTool(maxArchiveBytes: 5L * 1024 * 1024 * 1024);

        var ex = Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "big.zip" }), Ctx(ddb.Object), CancellationToken.None));
        ex.Message.ShouldContain("too large");
    }

    [Test]
    public void ValidateAsync_QuotaExceeded_Propagates()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, utils) = CreateTool();
        utils.Setup(u => u.CheckCurrentUserStorage(It.IsAny<long>()))
            .ThrowsAsync(new QuotaExceededException("quota exceeded"));

        Should.Throw<QuotaExceededException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void Plan_ReturnsUncompressedEstimate()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        var plan = tool.Plan(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object));

        plan.QuotaKey.ShouldBe("archive-extract");
        plan.EstimatedOutputBytes.ShouldBe(5); // "hello"
    }

    private static void CreateZip(string path, string entryName, string content)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
