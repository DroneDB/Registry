#nullable enable
using Registry.Web.Test.Support;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private static FileUrlImportTool CreateTool(ImportSettings? settings = null)
    {
        settings ??= new ImportSettings { SsrfAllowPrivateNetworks = true };
        settings.SsrfAllowPrivateNetworks = true; // keep SSRF checks hermetic (no DNS)

        var guard = new SsrfGuard(settings);
        var downloader = new GuardedHttpDownloader(guard, new SimpleFactory(),
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
    /// a ".ddb" segment at any depth (libddb never indexes it) and ".uploads" at the root. The old
    /// check only matched ".ddb" at the root.
    /// </summary>
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
    /// Only an exact ".ddb" segment is reserved, compared ordinally as libddb does
    /// (<c>containsDdbSegment</c>): ".DDB" and ".ddbx" are ordinary folders libddb indexes, and a
    /// nested ".uploads" is not the dataset's staging folder. The old check rejected ".DDB"
    /// (case-insensitive), unlike uploads and libddb.
    /// </summary>
    [TestCase(".DDB")]
    [TestCase(".ddbx")]
    [TestCase("photos/.uploads")]
    public async Task ValidateAsync_FolderResemblingReserved_DoesNotThrow(string folder)
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder, overwrite = false });

        await Should.NotThrowAsync(async () => await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
    }

    [Test]
    public async Task ValidateAsync_ExistingFileWithoutOverwrite_Throws()
    {
        var tool = CreateTool();
        var req = Req(new { url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder = "", overwrite = false });

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ValidateAsync(req, Ctx(entryExists: true), CancellationToken.None));
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
}
