using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Utilities;
using Registry.Web.Test.Baselines;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The two splat exports through HTTP, on the access fixture's private dataset (an owner, a read-only member, a
/// read-write member, a stranger): they are tools of the task API (<c>POST /tasks</c>), so a caller needs read access
/// like the other exports, a request libddb or the build state refuses is answered at submission (not as a failed task
/// later), the task is queued with its entry and its options, and the <c>.spz</c> is downloaded from the build-artifact
/// route by its role under the entry's name.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SplatExportHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SplatExportHttpTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    private const string SpzTool = "splat-spz-export";
    private const string TrainingTool = "splat-training-export";
    private const string Hash = "9e1f0c5a3b7d4e6f8a2b4c6d8e0f1a3b5c7d9e1f3a5b7c9d1e3f5a7b9c1d3e5f";

    private string _datasetFolder = null!;

    [OneTimeSetUp]
    public async Task FindDatasetFolder()
    {
        var internalRef = await WithContextAsync(context =>
            Task.FromResult(context.Datasets.Single(d => d.Slug == PrivateDs).InternalRef));
        using var scope = Factory.Services.CreateScope();
        _datasetFolder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(PrivateOrg, internalRef)
            .DatasetFolderPath;
    }

    [SetUp]
    public void ResetDdb()
    {
        Factory.DdbWrapper.Invocations.Clear();
        Factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, It.IsAny<string>())).Returns((Entry?)null);
    }

    private Task<(HttpResponseMessage Response, string Body, JsonObject? Json)> SubmitAsync(Caller caller,
        string toolId, string? path, string paramsJson = "{}") =>
        SendAsync(Request(HttpMethod.Post, Ds + "/tasks", caller, Json(
            $$"""{"toolId":"{{toolId}}","path":{{(path == null ? "null" : $"\"{path}\"")}},"params":{{paramsJson}},"force":true}""")));

    // A built splat: the streamed model exists (what a finished build leaves)
    private void BuiltSplat(string path, params BuildArtifact[] more)
    {
        Factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, path))
            .Returns(new Entry { Path = path, Hash = Hash, Type = EntryType.GaussianSplat, Size = 1 });
        Factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == path)))
            .Returns([TestBuilds.Build(_datasetFolder, path, EntryType.GaussianSplat, Hash, BuildState.Complete,
                [TestBuilds.File(ArtifactRoles.GsplatLod, "gsplat/model.rad", "application/octet-stream", "model.rad"),
                    .. more])]);
    }

    private void BuildFile(string relative, string content)
    {
        var file = Path.Combine(_datasetFolder, ".ddb", "build", Hash, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    // The jobs queued for a tool and the path they work on: the task API enqueues a Hangfire job whose arguments are
    // the tool, its version and its request
    private static List<(string Queue, JsonElement Request)> Queued(string toolId, string path)
    {
        var monitor = JobStorage.Current.GetMonitoringApi();
        return
        [
            .. monitor.Queues().SelectMany(q => monitor.EnqueuedJobs(q.Name, 0, 500)
                    .Where(j => j.Value.Job?.Args is [string id, ..] && id == toolId)
                    .Select(j => (q.Name, JsonDocument.Parse((string)j.Value.Job.Args[2]).RootElement)))
                .Where(j => j.RootElementPath() == path)
        ];
    }

    // ---------------------------------------------------------------- .spz export

    [TestCase(Caller.Anonymous, HttpStatusCode.Unauthorized)]
    [TestCase(Caller.Stranger, HttpStatusCode.Forbidden)]
    [TestCase(Caller.Reader, HttpStatusCode.Accepted)]
    [TestCase(Caller.Writer, HttpStatusCode.Accepted)]
    [TestCase(Caller.Owner, HttpStatusCode.Accepted)]
    public async Task SpzExport_NeedsReadAccess(Caller caller, HttpStatusCode expected)
    {
        BuiltSplat("scene.ply");

        var (response, body, _) = await SubmitAsync(caller, SpzTool, "scene.ply");

        response.StatusCode.ShouldBe(expected, body);
    }

    [Test]
    public async Task SpzExport_QueuesATaskForTheEntry_OnTheNativeBuildQueue()
    {
        BuiltSplat("queued/scene.ply");

        var (response, body, _) = await SubmitAsync(Caller.Reader, SpzTool, "queued/scene.ply");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        var queued = Queued(SpzTool, "queued/scene.ply").ShouldHaveSingleItem();
        queued.Queue.ShouldBe(JobQueues.Builds, "the conversion decodes the whole file in memory");
        queued.Request.GetProperty("OrgSlug").GetString().ShouldBe(PrivateOrg);
        queued.Request.GetProperty("DsSlug").GetString().ShouldBe(PrivateDs);
        queued.Request.GetProperty("ToolId").GetString().ShouldBe(SpzTool);
    }

    [Test]
    public async Task SpzExport_OfAnEntryThatIsNotThere_Is404()
    {
        Factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "gone.ply")))
            .Returns([TestBuilds.Missing("gone.ply")]);

        var (response, body, _) = await SubmitAsync(Caller.Reader, SpzTool, "gone.ply");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain("does not exist");
    }

    [Test]
    public async Task SpzExport_OfAnEntryThatIsNotASplat_Is400()
    {
        Factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "photo.jpg")))
            .Returns([TestBuilds.NotBuildable("photo.jpg", EntryType.Image)]);

        var (response, body, _) = await SubmitAsync(Caller.Reader, SpzTool, "photo.jpg");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain("not a Gaussian splat");
    }

    [TestCase(BuildState.Building)]
    [TestCase(BuildState.Pending)]
    [TestCase(BuildState.None)]
    public async Task SpzExport_OfASplatThatIsNotBuilt_Is404(BuildState state)
    {
        Factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "late.ply")))
            .Returns([TestBuilds.Build(_datasetFolder, "late.ply", EntryType.GaussianSplat, Hash, state)]);

        var (response, body, _) = await SubmitAsync(Caller.Reader, SpzTool, "late.ply");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain("is not built yet");
    }

    [Test]
    public async Task SpzExport_WithoutAPath_Is400()
    {
        var (response, _, _) = await SubmitAsync(Caller.Reader, SpzTool, null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- the download

    [Test]
    public async Task Download_ByRole_IsTheSpzNamedAfterTheEntry()
    {
        BuiltSplat("sub/scene.ply", TestBuilds.File(ArtifactRoles.GsplatExport, "gsplat/export.spz",
            "application/octet-stream", "export.spz"));
        BuildFile("gsplat/export.spz", "spz bytes");

        var (response, body, _) = await SendAsync(Request(HttpMethod.Get,
            $"{Ds}/build-artifact?path=sub/scene.ply&role={ArtifactRoles.GsplatExport}", Caller.Reader));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldBe("spz bytes");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("scene.spz", "named after the entry, not export.spz");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
    }

    [Test]
    public async Task Download_WithoutARole_IsTheFirstDownloadOfTheManifest_NamedAfterTheEntry()
    {
        BuiltSplat("scene.ply", TestBuilds.File(ArtifactRoles.GsplatExport, "gsplat/export.spz",
            "application/octet-stream", "export.spz"));
        BuildFile("gsplat/model.rad", "rad bytes");
        BuildFile("gsplat/export.spz", "spz bytes");

        var (response, body, _) = await SendAsync(Request(HttpMethod.Get, $"{Ds}/build-artifact?path=scene.ply", Caller.Reader));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldBe("rad bytes");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("scene.rad");
    }

    [Test]
    public async Task Download_ByRole_BeforeTheExportIsMade_Is404()
    {
        BuiltSplat("scene.ply");

        var (response, body, _) = await DownloadAsync("scene.ply", ArtifactRoles.GsplatExport);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain("made on demand: run its export first");
    }

    private Task<(HttpResponseMessage Response, string Body, JsonObject? Json)> DownloadAsync(string path, string role) =>
        SendAsync(Request(HttpMethod.Get, $"{Ds}/build-artifact?path={path}&role={role}", Caller.Reader));

    // Only a role made on demand tells to run its export: the other misses each say what is missing

    [Test]
    public async Task Download_ByRole_OfAnArtifactThatIsNotADownload_Is400()
    {
        // The bounds of a splat exist after its build, but the manifest offers them for no download (no downloadName)
        BuiltSplat("scene.ply", TestBuilds.File(ArtifactRoles.GsplatBounds, "gsplat/bounds.json", "application/json"));

        var (response, body, _) = await DownloadAsync("scene.ply", ArtifactRoles.GsplatBounds);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain($"The '{ArtifactRoles.GsplatBounds}' artifact of entry 'scene.ply' cannot be downloaded");
        body.ShouldNotContain("made on demand");
    }

    [Test]
    public async Task Download_ByRole_ThatTheEntryTypeDoesNotHave_Is404()
    {
        BuiltSplat("scene.ply");

        var (response, body, _) = await DownloadAsync("scene.ply", ArtifactRoles.RasterCog);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain($"Entry 'scene.ply' has no '{ArtifactRoles.RasterCog}' artifact");
        body.ShouldNotContain("made on demand");
    }

    [Test]
    public async Task Download_ByRole_OfAnEntryThatIsNotBuildable_Is404()
    {
        Factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, "photo.jpg"))
            .Returns(new Entry { Path = "photo.jpg", Hash = Hash, Type = EntryType.Image, Size = 1 });
        Factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "photo.jpg")))
            .Returns([TestBuilds.NotBuildable("photo.jpg", EntryType.Image)]);

        var (response, body, _) = await DownloadAsync("photo.jpg", ArtifactRoles.GsplatLod);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain($"Entry 'photo.jpg' has no '{ArtifactRoles.GsplatLod}' artifact");
    }

    [Test]
    public async Task Download_ByRole_OfAnAbsentArtifactOfACompleteBuild_ThatIsNotMadeOnDemand_Is404()
    {
        // A splat built with the level-of-detail model has no legacy .spz file
        BuiltSplat("scene.ply");

        var (response, body, _) = await DownloadAsync("scene.ply", ArtifactRoles.GsplatSpz);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain($"Entry 'scene.ply' has no '{ArtifactRoles.GsplatSpz}' artifact");
        body.ShouldNotContain("made on demand");
    }

    [TestCase(BuildState.Building, ArtifactRoles.GsplatLod)]
    [TestCase(BuildState.Pending, ArtifactRoles.GsplatLod)]
    [TestCase(BuildState.None, ArtifactRoles.GsplatLod)]
    [TestCase(BuildState.None, ArtifactRoles.GsplatExport)]
    public async Task Download_ByRole_OfASplatThatIsNotBuilt_Is404(BuildState state, string role)
    {
        Factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, "late.ply"))
            .Returns(new Entry { Path = "late.ply", Hash = Hash, Type = EntryType.GaussianSplat, Size = 1 });
        Factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "late.ply")))
            .Returns([TestBuilds.Build(_datasetFolder, "late.ply", EntryType.GaussianSplat, Hash, state)]);

        var (response, body, _) = await DownloadAsync("late.ply", role);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain("Entry 'late.ply' is not built yet");
        body.ShouldNotContain("made on demand");
    }

    [TestCase(Caller.Anonymous, HttpStatusCode.Unauthorized)]
    [TestCase(Caller.Stranger, HttpStatusCode.Forbidden)]
    public async Task Download_ByRole_NeedsReadAccess(Caller caller, HttpStatusCode expected)
    {
        BuiltSplat("scene.ply", TestBuilds.File(ArtifactRoles.GsplatExport, "gsplat/export.spz",
            downloadName: "export.spz"));

        var (response, _, _) = await SendAsync(Request(HttpMethod.Get,
            $"{Ds}/build-artifact?path=scene.ply&role={ArtifactRoles.GsplatExport}", caller));

        response.StatusCode.ShouldBe(expected);
    }

    // ---------------------------------------------------------------- training data export

    private void ProjectFolder(string path) =>
        Factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, path))
            .Returns(new Entry { Path = path, Hash = "", Type = EntryType.Directory, Size = 0 });

    [TestCase(Caller.Anonymous, HttpStatusCode.Unauthorized)]
    [TestCase(Caller.Stranger, HttpStatusCode.Forbidden)]
    [TestCase(Caller.Reader, HttpStatusCode.Accepted)]
    [TestCase(Caller.Writer, HttpStatusCode.Accepted)]
    public async Task TrainingExport_NeedsReadAccess(Caller caller, HttpStatusCode expected)
    {
        ProjectFolder("odm");

        var (response, body, _) = await SubmitAsync(caller, TrainingTool, "odm");

        response.StatusCode.ShouldBe(expected, body);
    }

    [Test]
    public async Task TrainingExport_QueuesATaskWithItsFolderAndOptions_OnTheNativeBuildQueue()
    {
        ProjectFolder("flights/odm");

        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, "flights/odm",
            """{"maxImageSize":1600,"maxPoints":50000}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        var queued = Queued(TrainingTool, "flights/odm").ShouldHaveSingleItem();
        queued.Queue.ShouldBe(JobQueues.Builds);
        var parameters = queued.Request.GetProperty("Params");
        parameters.GetProperty("maxImageSize").GetInt32().ShouldBe(1600);
        parameters.GetProperty("maxPoints").GetInt32().ShouldBe(50000);
    }

    [Test]
    public async Task TrainingExport_OfTheDatasetRoot_NeedsNoFolderEntry()
    {
        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
    }

    [Test]
    public async Task TrainingExport_OfAFolderThatIsNotThere_Is404()
    {
        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, "missing-odm");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain("does not exist");
    }

    [Test]
    public async Task TrainingExport_OfAFile_Is400()
    {
        Factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, "cameras.json"))
            .Returns(new Entry { Path = "cameras.json", Hash = "1", Type = EntryType.Generic, Size = 1 });

        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, "cameras.json");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain("not a folder");
    }

    [TestCase("""{"maxPoints":-1}""", "maxPoints")]
    [TestCase("""{"maxImageSize":-5}""", "maxImageSize")]
    [TestCase("""{"maxImageSize":4294967296}""", "maxImageSize")]
    [TestCase("""{"maxImageSize":1048577}""", "maxImageSize")]
    [TestCase("""{"maxPoints":10000001}""", "maxPoints")]
    public async Task TrainingExport_WithAnOptionOutOfRange_Is400(string parameters, string option)
    {
        ProjectFolder("odm");

        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, "odm", parameters);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain(option);
    }

    // libddb refuses more points (or a longer image side) than these: the largest it accepts is queued
    [TestCase("""{"maxPoints":10000000}""")]
    [TestCase("""{"maxImageSize":1048576}""")]
    public async Task TrainingExport_WithAnOptionAtLibddbsLimit_IsQueued(string parameters)
    {
        ProjectFolder("odm");

        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, "odm", parameters);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
    }

    [Test]
    public async Task TrainingExport_OverTheLimitOfPoints_SaysTheLimit()
    {
        ProjectFolder("odm");

        var (response, body, _) = await SubmitAsync(Caller.Reader, TrainingTool, "odm", """{"maxPoints":10000001}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain($"from 0 to {SplatTrainingExportTool.MaxPointsLimit}");
    }
}

internal static class QueuedRequests
{
    // The path a queued request works on
    public static string? RootElementPath(this (string Queue, JsonElement Request) job) =>
        job.Request.GetProperty("Path").GetString();
}
