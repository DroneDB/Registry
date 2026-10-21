#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Import;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-10: the file URL import downloads onto the dataset's volume (its uploads folder), so the move
/// into place is a rename and the disk guard measures the volume the download fills. Before, the
/// download went to <c>Path.GetTempPath()</c> (the container layer on prod) and the move into the
/// dataset (RAID5) was a full copy.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq10FileUrlImportVolumeTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq10FileUrlImportVolumeTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)i).ToArray();

    private string _dataset = null!;
    private string Uploads => Path.Combine(_dataset, IDDB.UploadsFolderName);

    [SetUp]
    public void SetUp()
    {
        _dataset = Path.Combine(Path.GetTempPath(), "rq10-fileimport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataset);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataset))
            Directory.Delete(_dataset, true);
    }

    [Test]
    public async Task Execute_DownloadsIntoTheDatasetUploadsFolder_AndMovesTheFileIntoPlace()
    {
        string[]? partialFiles = null;
        var handler = new PayloadHandler(() => partialFiles = Directory.Exists(Uploads) ? Directory.GetFiles(Uploads) : []);
        var tool = CreateTool(handler);

        await tool.ExecuteAsync(Request("photos"), Ctx(out var ddb), new Progress<HeavyToolProgress>(), CancellationToken.None);

        // While the body streamed, the partial file was in the dataset's uploads folder
        partialFiles.ShouldNotBeNull();
        partialFiles.Length.ShouldBe(1);
        File.ReadAllBytes(Path.Combine(_dataset, "photos", "cloud.laz")).ShouldBe(Payload);
        Directory.GetFiles(Uploads).ShouldBeEmpty("the download was moved, not copied");
        ddb.Verify(d => d.AddRaw("photos/cloud.laz"), Times.Once);
    }

    [Test]
    public async Task Execute_FailedDownload_LeavesNothingBehind()
    {
        var tool = CreateTool(new PayloadHandler(() => { }, HttpStatusCode.InternalServerError));

        await Should.ThrowAsync<HttpRequestException>(() =>
            tool.ExecuteAsync(Request(""), Ctx(out _), new Progress<HeavyToolProgress>(), CancellationToken.None));

        File.Exists(Path.Combine(_dataset, "cloud.laz")).ShouldBeFalse();
        (Directory.Exists(Uploads) ? Directory.GetFiles(Uploads) : []).ShouldBeEmpty();
    }

    private static FileUrlImportTool CreateTool(HttpMessageHandler handler)
    {
        var settings = new ImportSettings { SsrfAllowPrivateNetworks = true };
        var guard = new SsrfGuard(settings);
        var downloader = new GuardedHttpDownloader(guard, new HandlerFactory(handler),
            TestHeadroom.Default,
            NullLogger<GuardedHttpDownloader>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            Import = settings,
            ProcessingPlatform = new ProcessingPlatformSettings { DiskSafetyMarginBytes = 0 }
        });

        return new FileUrlImportTool(downloader, guard, Mock.Of<IImportCredentialProtector>(),
            Mock.Of<IServiceScopeFactory>(), options, TestPaths.Policy, TestScratch.Provider,
            NullLogger<FileUrlImportTool>.Instance);
    }

    private IHeavyToolExecutionContext Ctx(out Mock<IDDB> ddb)
    {
        ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dataset);
        ddb.Setup(d => d.GetLocalPath(It.IsAny<string>()))
            .Returns((string p) => Path.Combine(_dataset, p.Replace('/', Path.DirectorySeparatorChar)));

        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.Logger).Returns(NullLogger.Instance);
        return ctx.Object;
    }

    private static HeavyToolRequest Request(string folder) => new("import-file", "1", "org", "ds", null,
        JsonSerializer.SerializeToElement(new
        {
            url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder, overwrite = false
        }));

    // Serves the payload; onBodyRead runs after the client read the whole body (the file is still partial)
    private sealed class PayloadHandler(Action onBodyRead, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(new NotifyingStream(Payload, onBodyRead)) });
    }

    private sealed class NotifyingStream(byte[] data, Action atEnd) : MemoryStream(data)
    {
        private bool _notified;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            if (read == 0 && !_notified)
            {
                _notified = true;
                atEnd();
            }
            return read;
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
