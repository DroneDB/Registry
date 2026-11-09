using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Test.Common;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The file URL import downloads onto the dataset's volume (its uploads folder), so the move
/// into place is a rename and the disk guard measures the volume the download fills. Before, the
/// download went to <c>Path.GetTempPath()</c> (the container layer on prod) and the move into the
/// dataset (RAID5) was a full copy.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~FileUrlImportVolumeTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class FileUrlImportVolumeTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)i).ToArray();

    private string _dataset = null!;
    private string Uploads => Path.Combine(_dataset, IDDB.UploadsFolderName);

    [SetUp]
    public void SetUp()
    {
        Recorder = new FakeTaskWriteRecorder();
        _dataset = Path.Combine(Path.GetTempPath(), "fileurlimportvolume-fileimport-" + Guid.NewGuid().ToString("N"));
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
        ddb.Verify(d => d.AddRawBatch(It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == "photos/cloud.laz")), Times.Once);
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

    // FR-XC-27: the file the task creates and the folders it creates for it are in its write record, before they exist;
    // the stamp is taken from the complete file in the uploads folder, before the move
    [Test]
    public async Task Execute_NewFile_RecordsTheFileAndItsNewFolder_StampedBeforeTheMove()
    {
        var tool = CreateTool(new PayloadHandler(() => { }));
        var ctx = Ctx(out _);

        await tool.ExecuteAsync(Request("photos/2026"), ctx, new Progress<HeavyToolProgress>(), CancellationToken.None);

        Recorder.Paths.ShouldBe(["photos/2026/cloud.laz"]);
        Recorder.Folders.ShouldBe(["photos", "photos/2026"]);
        Recorder.Stamped.Keys.ShouldBe(["photos/2026/cloud.laz"]);
        Recorder.Stamped["photos/2026/cloud.laz"].ShouldStartWith(Uploads);
        Recorder.Flushes.ShouldBeGreaterThan(0, "the record is written before the file is moved");
    }

    [Test]
    public async Task Execute_AFolderThatExists_IsNotRecorded()
    {
        Directory.CreateDirectory(Path.Combine(_dataset, "photos"));
        var tool = CreateTool(new PayloadHandler(() => { }));

        await tool.ExecuteAsync(Request("photos"), Ctx(out _), new Progress<HeavyToolProgress>(), CancellationToken.None);

        Recorder.Folders.ShouldBeEmpty();
        Recorder.Paths.ShouldBe(["photos/cloud.laz"]);
    }

    // FR-XC-07: a file the request replaces is never recorded: a rollback must not delete what was there
    [Test]
    public async Task Execute_OverwriteOfAnExistingFile_ReplacesIt_AndDoesNotRecordIt()
    {
        Directory.CreateDirectory(Path.Combine(_dataset, "photos"));
        File.WriteAllText(Path.Combine(_dataset, "photos", "cloud.laz"), "original");
        var tool = CreateTool(new PayloadHandler(() => { }));

        await tool.ExecuteAsync(Request("photos", overwrite: true), Ctx(out _), new Progress<HeavyToolProgress>(),
            CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_dataset, "photos", "cloud.laz")).ShouldBe(Payload);
        Recorder.Paths.ShouldBeEmpty();
        Recorder.Folders.ShouldBeEmpty();
        Recorder.Stamped.ShouldBeEmpty();
    }

    // A file that appeared since the check of the worker is never replaced (no overwrite was asked), and never recorded
    [Test]
    public async Task Execute_AFileThatAppearsBeforeTheMove_IsKept_AndNeverRecorded()
    {
        var target = Path.Combine(_dataset, "cloud.laz");
        var tool = CreateTool(new PayloadHandler(() => File.WriteAllText(target, "somebody else's")));

        await Should.ThrowAsync<NameClashException>(() =>
            tool.ExecuteAsync(Request(""), Ctx(out _), new Progress<HeavyToolProgress>(), CancellationToken.None));

        File.ReadAllText(target).ShouldBe("somebody else's");
        Recorder.Paths.ShouldBeEmpty();
        Recorder.Forgotten.ShouldBeEmpty();
        Directory.GetFiles(Uploads).ShouldBeEmpty();
    }

    [Test]
    public void DeclaresThatItAddsDatasetFiles() => CreateTool(new PayloadHandler(() => { })).AddsDatasetFiles.ShouldBeTrue();

    private static FileUrlImportTool CreateTool(HttpMessageHandler handler)
    {
        var settings = new ImportSettings { SsrfAllowPrivateNetworks = true };
        var guard = new SsrfGuard(settings);
        var downloader = new GuardedHttpDownloader(guard, new HandlerFactory(handler),
            TestHeadroom.Default);
        var options = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            Import = settings,
            ProcessingPlatform = new ProcessingPlatformSettings { DiskSafetyMarginBytes = 0 }
        });

        return new FileUrlImportTool(downloader, new ImportCredentials(Mock.Of<IImportCredentialProtector>()),
            Support.TestJobs.FileIndexer(), TestSettings.Options<ImportSettings>(options), TestSettings.Options<ProcessingPlatformSettings>(options), TestPaths.Policy, TestScratch.Provider);
    }

    private FakeTaskWriteRecorder Recorder { get; set; } = new();

    private IHeavyToolExecutionContext Ctx(out Mock<IDDB> ddb)
    {
        ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dataset);
        ddb.Setup(d => d.GetLocalPath(It.IsAny<string>()))
            .Returns((string p) => Path.Combine(_dataset, p.Replace('/', Path.DirectorySeparatorChar)));
        ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> paths) => [.. paths.Select(p => new Entry { Path = p, Type = EntryType.Generic })]);
        Support.TestBuilds.SetupBuilds(ddb);

        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("org", Guid.NewGuid()));
        ctx.SetupGet(c => c.WriteRecord).Returns(Recorder);
        ctx.SetupGet(c => c.BuildQueue).Returns(Support.TestJobs.Scheduler());
        return ctx.Object;
    }

    private static HeavyToolRequest Request(string folder, bool overwrite = false) => new("import-file", "1", "org", "ds", null,
        JsonSerializer.SerializeToElement(new
        {
            url = "http://download.test/cloud.laz", fileName = "cloud.laz", folder, overwrite
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
