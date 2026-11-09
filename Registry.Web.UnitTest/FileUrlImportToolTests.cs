using Registry.Web.Test.Support;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Import;
using Registry.Web.Exceptions;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="FileUrlImportTool"/> validation and planning: URL/extension/path checks and
/// the overwrite conflict guard. The actual download is exercised through
/// <see cref="GuardedHttpDownloaderTests"/>.
/// </summary>
[TestFixture]
public class FileUrlImportToolTests
{
    private static FileUrlImportTool CreateTool(ImportSettings? settings = null, IHttpClientFactory? http = null)
    {
        settings ??= new ImportSettings { SsrfAllowPrivateNetworks = true };
        settings.SsrfAllowPrivateNetworks = true; // keep SSRF checks hermetic (no DNS)

        var guard = new SsrfGuard(settings);
        var downloader = new GuardedHttpDownloader(guard, http ?? new SimpleFactory(),
            TestHeadroom.Default);
        var options = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            Import = settings,
            ProcessingPlatform = new ProcessingPlatformSettings()
        });

        return new FileUrlImportTool(downloader, new ImportCredentials(Mock.Of<IImportCredentialProtector>()),
            TestJobs.FileIndexer(), TestSettings.Options<ImportSettings>(options), TestSettings.Options<ProcessingPlatformSettings>(options), TestPaths.Policy, TestScratch.Provider);
    }

    private static IHeavyToolValidationContext Ctx(bool entryExists = false)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns(entryExists);

        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.Setup(c => c.Ddb).Returns(ddb.Object);
        return ctx.Object;
    }

    private static HeavyToolRequest Req(object p)
        => new("import-file", "1", "org", "ds", null, JsonSerializer.SerializeToElement(p));

    [Test]
    public async Task ValidateAsync_ValidRequest_DoesNotThrow()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = false });

        await Should.NotThrowAsync(async () => await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    [Test]
    public async Task ValidateAsync_BlockedExtension_Throws()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/evil.exe", fileName = "evil.exe", folder = "", overwrite = false });

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    [Test]
    public async Task ValidateAsync_InvalidUrl_Throws()
    {
        var tool = CreateTool();
        var req = Req(new { url = "ftp://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = false });

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    [TestCase("../secret")]
    [TestCase("a/../../b")]
    [TestCase(".ddb")]
    [TestCase(".ddb/nested")]
    public async Task ValidateAsync_UnsafeFolder_Throws(string folder)
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder, overwrite = false });

        await Should.ThrowAsync<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    /// <summary>
    /// The destination applies the shared reserved rule (<see cref="Registry.Web.Utilities.DatasetPathPolicy.IsReserved"/>):
    /// a segment a file system opens as the ".ddb" folder, at any depth (libddb never indexes it),
    /// whatever its letter case, and ".uploads" at the root. The old check only matched ".ddb" at
    /// the root.
    /// </summary>
    [TestCase(".DDB")]
    [TestCase("sub/.ddb")]
    [TestCase("a/b/.ddb/c")]
    [TestCase(".uploads")]
    [TestCase(".uploads/quarantine")]
    public async Task ValidateAsync_ReservedFolder_Throws(string folder)
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder, overwrite = false });

        await Should.ThrowAsync<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    /// <summary>
    /// A longer name is not the ".ddb" folder: ".ddbx" is an ordinary folder libddb indexes, and a
    /// nested ".uploads" is not the dataset's staging folder.
    /// </summary>
    [TestCase(".ddbx")]
    [TestCase("photos/.uploads")]
    public async Task ValidateAsync_FolderResemblingReserved_DoesNotThrow(string folder)
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder, overwrite = false });

        await Should.NotThrowAsync(async () => await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    [Test]
    public async Task ValidateAsync_ExistingFileWithoutOverwrite_Is409WithItsPath()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "scans", overwrite = false });

        var ex = await Should.ThrowAsync<NameClashException>(async () =>
            await tool.ValidateAsync(req, Ctx(entryExists: true), CancellationToken.None));

        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldBe("A file or folder named 'cloud.laz' already exists in this folder");
        ex.ConflictPath.ShouldBe("scans/cloud.laz");
    }

    [Test]
    public async Task ValidateAsync_ExistingFileInAnotherLetterCase_Is409WithTheExistingPath()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q =>
            string.IsNullOrEmpty(q.Path)
                ? [new EntrySummary { Path = "Cloud.LAZ", Type = EntryType.PointCloud }]
                : []);
        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.Setup(c => c.Ddb).Returns(ddb.Object);
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = false });

        var ex = await Should.ThrowAsync<NameClashException>(async () =>
            await CreateTool().ValidateAsync(req, ctx.Object, CancellationToken.None));

        ex.ConflictPath.ShouldBe("Cloud.LAZ");
    }

    [Test]
    public void DeclaresItsTargetAsOutputAndLabel()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/x", fileName = "cloud.laz", folder = "scans/" });

        tool.InputPaths(req).ShouldBeEmpty();
        tool.OutputPaths(req).ShouldBe(["scans/cloud.laz"]);
        tool.Label(req).ShouldBe("\u2192 scans/cloud.laz");
    }

    [Test]
    public async Task LaterAttempt_AfterTheMove_SkipsTheCheckAndTheDownload_AndIndexes()
    {
        // The first attempt moved the file into place, then failed to index it: the file exists, no overwrite
        var tool = CreateTool(http: new RefusingFactory());
        var written = System.IO.Path.GetTempFileName();
        try
        {
            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.EntryExists("cloud.laz")).Returns(true);
            ddb.Setup(d => d.GetLocalPath("cloud.laz")).Returns(written);
            ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()))
                .Returns((IReadOnlyList<string> paths) => [.. paths.Select(p => new Entry { Path = p, Type = EntryType.Generic })]);
            TestBuilds.SetupBuilds(ddb);
            var ctx = new Mock<IHeavyToolExecutionContext>();
            ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
            ctx.SetupGet(c => c.TaskId).Returns("task-1");
            ctx.SetupGet(c => c.BuildQueue).Returns(TestJobs.Scheduler());
            ctx.SetupGet(c => c.PublishedOutput).Returns("cloud.laz");
            var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = false });

            await tool.ValidateAsync(req, ctx.Object, CancellationToken.None);
            await tool.ExecuteAsync(req, ctx.Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

            ddb.Verify(d => d.AddRawBatch(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "cloud.laz" }))), Times.Once);
            ctx.Verify(c => c.MarkOutputPublished(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            System.IO.File.Delete(written);
        }
    }

    [Test]
    public async Task MarkerWhoseFileIsGone_DoesNotExcuseAnExistingName()
    {
        // The marker names the target, but its file is gone: the existing index entry is refused again
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.EntryExists("cloud.laz")).Returns(true);
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.PublishedOutput).Returns("cloud.laz");
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = false });

        await Should.ThrowAsync<NameClashException>(() =>
            CreateTool().ValidateAsync(req, ctx.Object, CancellationToken.None));
    }

    [Test]
    public async Task ValidateAsync_ExistingFileWithOverwrite_DoesNotThrow()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = true });

        await Should.NotThrowAsync(async () =>
            await tool.ValidateAsync(req, Ctx(entryExists: true), CancellationToken.None));
    }

    [Test]
    public void Plan_ReturnsSizeHintAndQuotaKey()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", sizeBytes = 999L });

        var plan = tool.Plan(req, Ctx());

        plan.EstimatedOutputBytes.ShouldBe(999);
        plan.QuotaKey.ShouldBe("import-file");
    }

    [Test]
    public void Metadata_IsStable()
    {
        var tool = CreateTool();
        tool.Id.ShouldBe("import-file");
        tool.RequiredAccess.ShouldBe(HeavyToolPermission.Write);
        tool.ProducesArtifact.ShouldBeFalse();
    }

    private sealed class SimpleFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Fails the test when the tool downloads anything
    private sealed class RefusingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new AssertionException("the file was downloaded again");
    }
}
