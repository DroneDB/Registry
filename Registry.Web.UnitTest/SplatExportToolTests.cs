#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// What the two splat tools do with libddb's answers when they run in their task: the training data is archived as
/// libddb wrote it (its model, its images, its georeference) under a name derived from the folder, its warnings reach
/// the log of the task, a failure is explained in the log, and a build lock held by another build or export is left to
/// the job's retry policy instead of failing the task.
/// </summary>
[TestFixture]
public sealed class SplatExportToolTests
{
    private string _work = null!;

    [SetUp]
    public void SetUp() => _work = Directory.CreateTempSubdirectory("splat_export_tool_").FullName;

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private sealed class Log : IProgress<HeavyToolProgress>
    {
        public List<HeavyToolProgress> Items { get; } = [];
        public void Report(HeavyToolProgress value) => Items.Add(value);
        public string[] Lines => [.. Items.Where(i => i.LogChunk != null).Select(i => i.LogChunk!)];
    }

    private static HeavyToolRequest Request(string tool, string? path, string paramsJson = "{}") =>
        new(tool, "1", "o", "d", path, JsonDocument.Parse(paramsJson).RootElement);

    private IHeavyToolExecutionContext Context(Mock<IDDB> ddb)
    {
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.WorkDir).Returns(_work);
        return ctx.Object;
    }

    // ---------------------------------------------------------------- training data

    // What libddb leaves in the folder it is given
    private static void WriteModel(string outFolder)
    {
        Directory.CreateDirectory(Path.Combine(outFolder, "sparse", "0"));
        Directory.CreateDirectory(Path.Combine(outFolder, "images"));
        foreach (var bin in new[] { "cameras.bin", "images.bin", "points3D.bin" })
            File.WriteAllText(Path.Combine(outFolder, "sparse", "0", bin), bin);
        File.WriteAllText(Path.Combine(outFolder, "images", "DJI_0001.JPG"), "jpeg");
        File.WriteAllText(Path.Combine(outFolder, "georef.json"), """{"crs":"EPSG:32633","offset":[500000,4600000,0]}""");
    }

    private static Mock<IDDB> Exporting(Action<string>? write = null, string[]? warnings = null)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatTrainingData(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SplatTrainingOptions?>(), It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string outFolder, SplatTrainingOptions? _, Action<double, string?>? progress,
                CancellationToken _) =>
            {
                (write ?? WriteModel)(outFolder);
                progress?.Invoke(0.2, "cameras");
                progress?.Invoke(0.5, "points");
                progress?.Invoke(0.6, "points");
                progress?.Invoke(1, "images");
                return new SplatTrainingExport { Warnings = warnings ?? [], Images = 1, Points = 3 };
            });
        return ddb;
    }

    private static SplatTrainingExportTool TrainingTool() => new(new ZipArchiveBuilder(), new FileSystem());

    [Test]
    public async Task Training_ArchivesWhatLibddbWrote_AsOneZipOfTheFolderName()
    {
        var ddb = Exporting();
        var log = new Log();

        var artifact = (await TrainingTool().ExecuteAsync(Request("splat-training-export", "flights/odm-1"),
            Context(ddb), log, CancellationToken.None))!;

        artifact.FileName.ShouldBe("odm-1-training-data.zip");
        artifact.RelativePath.ShouldBe(artifact.FileName);
        artifact.ContentType.ShouldBe("application/zip");
        var archive = Path.Combine(_work, artifact.FileName);
        artifact.SizeBytes.ShouldBe(new FileInfo(archive).Length);
        artifact.Sha256.ShouldBe(await FileHash.Sha256Async(archive, CancellationToken.None));
        using (var zip = ZipFile.OpenRead(archive))
            zip.Entries.Select(e => e.FullName).Order().ShouldBe(
                ["georef.json", "images/DJI_0001.JPG", "sparse/0/cameras.bin", "sparse/0/images.bin", "sparse/0/points3D.bin"]);

        // The task's folder keeps the one file it serves, not libddb's copy of the model
        Directory.GetFileSystemEntries(_work).Select(Path.GetFileName).ShouldBe([artifact.FileName]);
    }

    [Test]
    public async Task Training_ExportsTheFolderWithTheOptions_AndTheRootAsADot()
    {
        var ddb = Exporting();
        SplatTrainingOptions? received = null;
        string? folder = null;
        ddb.Setup(d => d.ExportSplatTrainingData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SplatTrainingOptions?>(),
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string f, string outFolder, SplatTrainingOptions? o, Action<double, string?>? _, CancellationToken _) =>
            {
                folder = f;
                received = o;
                WriteModel(outFolder);
            })
            .Returns(new SplatTrainingExport());

        await TrainingTool().ExecuteAsync(Request("splat-training-export", null,
            """{"maxImageSize":1600,"maxPoints":50000}"""), Context(ddb), new Log(), CancellationToken.None);

        folder.ShouldBe(".");
        received.ShouldBe(new SplatTrainingOptions(1600, 50000));
        File.Exists(Path.Combine(_work, "d-training-data.zip")).ShouldBeTrue("the root is named after the dataset");
    }

    [Test]
    public async Task Training_PassesTheTaskToken_ToTheNativeCall()
    {
        var ddb = Exporting();
        using var cts = new CancellationTokenSource();

        await TrainingTool().ExecuteAsync(Request("splat-training-export", "odm"), Context(ddb), new Log(), cts.Token);

        ddb.Verify(d => d.ExportSplatTrainingData("odm", It.IsAny<string>(), It.IsAny<SplatTrainingOptions?>(),
            It.IsAny<Action<double, string?>?>(), cts.Token), Times.Once);
    }

    [Test]
    public async Task Training_WarningsReachTheLog_AndProgressStaysWithinTheExportShare()
    {
        var ddb = Exporting(warnings: ["2 images look straight down", "fisheye lens approximated"]);
        var log = new Log();

        await TrainingTool().ExecuteAsync(Request("splat-training-export", "odm"), Context(ddb), log, CancellationToken.None);

        log.Lines.ShouldContain("Warning: 2 images look straight down");
        log.Lines.ShouldContain("Warning: fisheye lens approximated");
        // One line per phase, not per report
        log.Lines.Count(l => l == "Phase: points").ShouldBe(1);
        log.Items.Where(i => i.Phase == "images").ShouldAllBe(i => i.Fraction <= 0.9 + 1e-9, "archiving is the last tenth");
        log.Items.Last().Fraction.ShouldBe(1);
    }

    [TestCase("../../etc/passwd", "passwd.zip")]
    [TestCase("my-model", "my-model.zip")]
    [TestCase("my-model.ZIP", "my-model.ZIP")]
    public async Task Training_NamesTheArchiveAfterTheRequest_WithoutADirectory(string requested, string expected)
    {
        var ddb = Exporting();

        var artifact = await TrainingTool().ExecuteAsync(
            Request("splat-training-export", "odm", $$"""{"fileName":"{{requested}}"}"""), Context(ddb), new Log(),
            CancellationToken.None);

        artifact!.FileName.ShouldBe(expected);
        File.Exists(Path.Combine(_work, expected)).ShouldBeTrue();
    }

    [Test]
    public async Task Training_Failure_IsExplainedInTheLog_AndRethrown()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatTrainingData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SplatTrainingOptions?>(),
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Cannot export the training data of 'odm': coords.txt is missing"));
        var log = new Log();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => TrainingTool().ExecuteAsync(
            Request("splat-training-export", "odm"), Context(ddb), log, CancellationToken.None));

        ex.Message.ShouldContain("coords.txt");
        log.Lines.ShouldContain(l => l.StartsWith("Failed:") && l.Contains("coords.txt is missing"));
    }

    [Test]
    public async Task Training_Canceled_IsNotAFailureOfTheExport()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatTrainingData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SplatTrainingOptions?>(),
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Throws(new DdbCanceledException("canceled"));
        var log = new Log();

        await Should.ThrowAsync<OperationCanceledException>(() => TrainingTool().ExecuteAsync(
            Request("splat-training-export", "odm"), Context(ddb), log, CancellationToken.None));

        log.Lines.ShouldNotContain(l => l.StartsWith("Failed:"));
    }

    // ---------------------------------------------------------------- .spz

    [Test]
    public async Task Spz_ExportsTheEntry_AndLeavesTheFileInItsBuildFolder()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatSpz("sub/scene.ply", It.IsAny<CancellationToken>()))
            .Returns(new SplatSpzExport { Path = "/x/export.spz", Role = ArtifactRoles.GsplatExport, Cached = false });
        var log = new Log();
        using var cts = new CancellationTokenSource();

        var artifact = await new SplatSpzExportTool().ExecuteAsync(Request("splat-spz-export", "sub/scene.ply"),
            Context(ddb), log, cts.Token);

        artifact.ShouldBeNull("the artifact is the build folder's, downloaded by role");
        ddb.Verify(d => d.ExportSplatSpz("sub/scene.ply", cts.Token), Times.Once);
        log.Lines.ShouldContain("Export complete");
    }

    [Test]
    public async Task Spz_AnExportThatWasMadeAlready_IsReportedAsSuch()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatSpz(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new SplatSpzExport { Path = "/x/export.spz", Role = ArtifactRoles.GsplatExport, Cached = true });
        var log = new Log();

        await new SplatSpzExportTool().ExecuteAsync(Request("splat-spz-export", "scene.ply"), Context(ddb), log,
            CancellationToken.None);

        log.Lines.ShouldContain("The .spz file was already exported");
    }

    [Test]
    public async Task Spz_AFormatThatIsNotConverted_IsExplainedInTheLog()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatSpz(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new DdbInvalidArgumentsException("The format .ksplat is not converted to SPZ"));
        var log = new Log();

        await Should.ThrowAsync<DdbInvalidArgumentsException>(() => new SplatSpzExportTool().ExecuteAsync(
            Request("splat-spz-export", "scene.ksplat"), Context(ddb), log, CancellationToken.None));

        log.Lines.ShouldContain(l => l.StartsWith("Failed:") && l.Contains(".ksplat is not converted"));
    }

    [Test]
    public async Task Spz_ALockHeldByABuildOrAnotherExport_IsRethrownForTheJobsRetryPolicy()
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.ExportSplatSpz(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new DdbBuildInProgressException("A build of scene.ply is running"));
        var log = new Log();

        await Should.ThrowAsync<DdbBuildInProgressException>(() => new SplatSpzExportTool().ExecuteAsync(
            Request("splat-spz-export", "scene.ply"), Context(ddb), log, CancellationToken.None));

        log.Lines.ShouldNotContain(l => l.StartsWith("Failed:"), "the task is not over: the job runs again");
    }

    [Test]
    public void TheTaskJob_RetriesALockHeldByABuild_AndALowDisk()
    {
        // The export fails with these while the entry's build lock is held or the disk is nearly full: they must
        // reach Hangfire's retry filter (DdbRetriedJob), the one policy of every job that needs the build lock
        var retry = typeof(HeavyTaskJobWrapper).GetCustomAttribute<AutomaticRetryAttribute>(inherit: true)!;

        retry.OnlyOn.ShouldContain(typeof(DdbBuildInProgressException));
        retry.OnlyOn.ShouldContain(typeof(DdbRetryableException));
    }
}
