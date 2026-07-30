#nullable enable
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
            NullLogger<GuardedHttpDownloader>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            Import = settings,
            ProcessingPlatform = new ProcessingPlatformSettings()
        });

        return new FileUrlImportTool(downloader, guard, Mock.Of<IImportCredentialProtector>(),
            Mock.Of<IServiceScopeFactory>(), options, NullLogger<FileUrlImportTool>.Instance);
    }

    private static IHeavyToolValidationContext Ctx(bool entryExists = false)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns(entryExists);

        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.Setup(c => c.Ddb).Returns(ddb.Object);
        ctx.Setup(c => c.Logger).Returns(NullLogger.Instance);
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

        await Should.ThrowAsync<ArgumentException>(async () =>
            await tool.ValidateAsync(req, Ctx(), CancellationToken.None));
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
