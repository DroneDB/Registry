using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Hosting;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The build key (FR-XC-28, D-020; FR-PRC-23, FR-VWR-17) on the Registry side: the readers that name a build (the
/// artifact URLs, the copy, the pending markers) use libddb's key, an older libddb without it reads as the hash, and
/// the upgrade rescan (§8.3) rescans the datasets once and rebuilds only the entries whose key changed.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildKeyTests"
/// </summary>
[TestFixture]
public class BuildKeyTests
{
    private const string Folder = "/datasets/org/ds";
    private const string Hash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";
    private const string GeoKey = Hash + "-16a3603c564a75c2";

    // ---- The wire ---------------------------------------------------------------------------------------

    [Test]
    public void TheBuildObject_ReadsTheKeyLibddbNames_ElseTheHash()
    {
        var current = JsonSerializer.Deserialize<EntryBuild>(
            $$"""{"buildable":true,"source":"a.ply","hash":"{{Hash}}","key":"{{GeoKey}}","root":"/r","state":"none","retryAfter":null}""",
            NativeJson.Options)!;
        var older = JsonSerializer.Deserialize<EntryBuild>(
            $$"""{"buildable":true,"source":"a.ply","hash":"{{Hash}}","root":"/r","state":"none","retryAfter":null}""",
            NativeJson.Options)!;

        current.Key.ShouldBe(GeoKey);
        current.BuildKey.ShouldBe(GeoKey);
        current.Hash.ShouldBe(Hash);
        older.Key.ShouldBeNull();
        older.BuildKey.ShouldBe(Hash, "a libddb that predates the build key names the build by the hash");
    }

    [Test]
    public void ThePendingMarker_ReadsItsKey_ElseTheHash()
    {
        var markers = JsonSerializer.Deserialize<PendingBuildInfo[]>(
            $$"""
            [{"hash":"{{Hash}}","key":"{{GeoKey}}","path":"a.obj","missingDependencies":["a.mtl"],"lastAttempt":1,"retryAfter":61,"awaitingFiles":true},
             {"hash":"{{Hash}}","path":"b.obj","missingDependencies":[],"lastAttempt":1,"retryAfter":61,"awaitingFiles":false}]
            """, NativeJson.Options)!;

        markers[0].BuildKey.ShouldBe(GeoKey);
        markers[1].BuildKey.ShouldBe(Hash);
    }

    [Test]
    public void TheRescanResult_ReadsTheKeyAndWhetherItChanged_AnOlderLibddbHasNeither()
    {
        var results = JsonSerializer.Deserialize<RescanResult[]>(
            $$"""
            [{"path":"a.ply","hash":"{{Hash}}","success":true,"buildKey":"{{GeoKey}}","buildKeyChanged":true},
             {"path":"b.ply","hash":"{{Hash}}","success":true}]
            """, NativeJson.Options)!;

        results[0].BuildKey.ShouldBe(GeoKey);
        results[0].BuildKeyChanged.ShouldBeTrue();
        results[1].BuildKey.ShouldBeNull();
        results[1].BuildKeyChanged.ShouldBeFalse("an older libddb has no build to move");
    }

    // ---- The artifact URLs ----------------------------------------------------------------------------------

    // FR-VWR-17: the URL of an artifact names the build folder, so the copy in a folder with a georeference does not
    // open the build of the root copy
    [Test]
    public void ArtifactLinks_AreNamedByTheBuildKey()
    {
        var urls = TestPorts.Urls(Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = "https://hub.example" }));
        var root = TestBuilds.Keyed(Folder, "train.ply", EntryType.GaussianSplat, Hash, Hash, BuildState.Complete,
            TestBuilds.File("gsplat.lod", "gsplat/model.rad"));
        var geo = TestBuilds.Keyed(Folder, "geo1/train.ply", EntryType.GaussianSplat, Hash, GeoKey, BuildState.Complete,
            TestBuilds.File("gsplat.lod", "gsplat/model.rad"));
        var older = TestBuilds.Keyed(Folder, "old.ply", EntryType.GaussianSplat, Hash, Hash, BuildState.Complete,
            TestBuilds.File("gsplat.lod", "gsplat/model.rad"));
        older.Build!.Key = null;

        urls.ArtifactLinks("org", "ds", root).Single().Url
            .ShouldBe($"https://hub.example/orgs/org/ds/ds/build/{Hash}/gsplat/model.rad");
        urls.ArtifactLinks("org", "ds", geo).Single().Url
            .ShouldBe($"https://hub.example/orgs/org/ds/ds/build/{GeoKey}/gsplat/model.rad");
        urls.ArtifactLinks("org", "ds", older).Single().Url
            .ShouldBe($"https://hub.example/orgs/org/ds/ds/build/{Hash}/gsplat/model.rad");
    }

    // ---- The rescan of one dataset ------------------------------------------------------------------------

    private static readonly EntryType[] SplatAndModel = [EntryType.Model, EntryType.GaussianSplat];

    private sealed class RecordingBuilds : IRescanBuildRunner
    {
        public List<string> Built { get; } = [];
        public int Pending { get; private set; }

        public Task BuildDataset() => throw new InvalidOperationException("the dataset is never rebuilt");

        public Task BuildEntry(RescanResult entry)
        {
            Built.Add(entry.Path);
            return Task.CompletedTask;
        }

        public Task BuildPending()
        {
            Pending++;
            return Task.CompletedTask;
        }
    }

    private static RescanResult Rescanned(string path, bool changed, bool success = true) =>
        new() { Path = path, Hash = Hash, Success = success, BuildKey = changed ? GeoKey : Hash, BuildKeyChanged = changed };

    private static Mock<IDDB> DdbWith(IReadOnlyList<RescanResult> results, params string[] indexed)
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns(
            [.. indexed.Select(p => new EntrySummary { Path = p, Type = EntryType.GaussianSplat, Hash = Hash })]);
        ddb.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([.. results]);
        ddb.SetupBuilds([.. results.Select(r => TestBuilds.Build(Folder, r.Path, EntryType.GaussianSplat, Hash, BuildState.None))]);
        return ddb;
    }

    private static IndexRescanService Service(RegistryContext context, IDdbManager ddbManager) =>
        new(context, ddbManager, Mock.Of<ISystemTaskSubmitter>(),
            new RescanRebuild(Mock.Of<IDatasetCacheInvalidator>(), NullLogger<RescanRebuild>.Instance),
            NullLogger<IndexRescanService>.Instance);

    // §8.3 "Rescan test: only changed keys rebuild"
    [Test]
    public async Task RescanDatasetNow_RebuildsOnlyTheEntriesWhoseKeyChanged()
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var ddb = DdbWith([Rescanned("geo1/train.ply", changed: true), Rescanned("train.ply", changed: false),
            Rescanned("scene.ply", changed: false), Rescanned("geo2/train.ply", changed: true)],
            "geo1/train.ply", "train.ply", "scene.ply", "geo2/train.ply");
        var manager = Mock.Of<IDdbManager>(m => m.Get("org", It.IsAny<Guid>()) == ddb.Object);
        var builds = new RecordingBuilds();

        var outcome = await Service(context, manager).RescanDatasetNowAsync("org", "ds", Guid.NewGuid(),
            RescanFilter(), builds);

        builds.Built.ShouldBe(["geo1/train.ply", "geo2/train.ply"]);
        builds.Pending.ShouldBe(1);
        outcome.ShouldBe(new DatasetRescanOutcome(4, 0, 2, 0));
        // The rescan reads the matching files and does not stop at one it cannot read
        ddb.Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "model,gaussiansplat" && !o.StopOnError)), Times.Once);
    }

    [Test]
    public async Task RescanDatasetNow_AnEntryItCannotRead_IsReportedAndNotRebuilt()
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var ddb = DdbWith([Rescanned("a.ply", changed: true, success: false), Rescanned("b.ply", changed: true)],
            "a.ply", "b.ply");
        var manager = Mock.Of<IDdbManager>(m => m.Get("org", It.IsAny<Guid>()) == ddb.Object);
        var builds = new RecordingBuilds();

        var outcome = await Service(context, manager).RescanDatasetNowAsync("org", "ds", Guid.NewGuid(),
            RescanFilter(), builds);

        builds.Built.ShouldBe(["b.ply"]);
        outcome.Unreadable.ShouldBe(1);
    }

    [Test]
    public async Task RescanDatasetNow_ADatasetWithoutEntriesOfTheTypes_IsNotRescanned()
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var ddb = DdbWith([]);
        var manager = Mock.Of<IDdbManager>(m => m.Get("org", It.IsAny<Guid>()) == ddb.Object);

        var outcome = await Service(context, manager).RescanDatasetNowAsync("org", "ds", Guid.NewGuid(),
            RescanFilter(), new RecordingBuilds());

        outcome.ShouldBe(DatasetRescanOutcome.NothingToRescan);
        ddb.Verify(d => d.RescanIndex(It.IsAny<RescanOptions?>()), Times.Never);
    }

    // A libddb that predates the build key names no key in the entries it re-reads; a failed entry has none either way
    [Test]
    public async Task RescanDatasetNow_ALibddbWithoutTheBuildKey_IsReported()
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var older = DdbWith([OlderLibddb("a.ply"), OlderLibddb("b.ply")], "a.ply", "b.ply");
        var current = DdbWith([Rescanned("a.ply", changed: false), OlderLibddb("b.ply", success: false)], "a.ply", "b.ply");
        var builds = new RecordingBuilds();

        var olderOutcome = await Service(context, Mock.Of<IDdbManager>(m => m.Get("org", It.IsAny<Guid>()) == older.Object))
            .RescanDatasetNowAsync("org", "ds", Guid.NewGuid(), RescanFilter(), builds);
        var currentOutcome = await Service(context, Mock.Of<IDdbManager>(m => m.Get("org", It.IsAny<Guid>()) == current.Object))
            .RescanDatasetNowAsync("org", "ds", Guid.NewGuid(), RescanFilter(), builds);

        olderOutcome.LibddbPredatesBuildKey.ShouldBeTrue();
        olderOutcome.Rebuilt.ShouldBe(0);
        currentOutcome.LibddbPredatesBuildKey.ShouldBeFalse();
        builds.Built.ShouldBeEmpty();
    }

    // The element of a libddb from before the build key: no buildKey, no buildKeyChanged
    private static RescanResult OlderLibddb(string path, bool success = true) =>
        new() { Path = path, Hash = success ? Hash : null, Success = success, Error = success ? null : "unreadable" };

    private static RescanFilter RescanFilter() => UpgradeRescanSteps.FilterOf([new BuildKeyRescanStep()]);

    // ---- The upgrade job ------------------------------------------------------------------------------------

    private sealed class ExtraStep(params EntryType[] types) : IUpgradeRescanStep
    {
        public string Id => "extra";
        public IReadOnlyList<EntryType> EntryTypes { get; } = types;
    }

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    private sealed class JobFixture : IDisposable
    {
        private readonly string _storage = Path.Combine(Path.GetTempPath(), "upgrade-rescan-" + Guid.NewGuid().ToString("N"));
        public RegistryContext Context { get; } = TestDb.CreateContext<RegistryContext>();
        public Dictionary<Guid, Mock<IDDB>> Ddbs { get; } = [];
        public List<string> Queued { get; } = [];
        public Dictionary<string, Guid> Refs { get; } = [];

        public JobFixture(params (string Org, string Ds)[] datasets)
        {
            foreach (var group in datasets.GroupBy(d => d.Org))
            {
                var org = new Organization
                {
                    Slug = group.Key, Name = group.Key, CreationDate = DateTime.UtcNow, OwnerId = "owner"
                };
                Context.Organizations.Add(org);
                foreach (var (_, ds) in group)
                {
                    var dataset = new Dataset
                    {
                        Slug = ds, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org
                    };
                    Context.Datasets.Add(dataset);
                    Refs[$"{group.Key}/{ds}"] = dataset.InternalRef;
                    // Each dataset: one splat whose key changed and one that keeps its build
                    Ddbs[dataset.InternalRef] = DdbWith([Rescanned($"{ds}/geo.ply", true), Rescanned($"{ds}/root.ply", false)],
                        $"{ds}/geo.ply", $"{ds}/root.ply");
                }
            }

            Context.SaveChanges();
        }

        public string StatePath => Path.Combine(_storage, MagicStrings.UpgradeRescansFileName);

        public string LockPath => Path.Combine(_storage, MagicStrings.UpgradeRescansLockFileName);

        public string RefOf(string name) => Refs[name].ToString("N");

        /// <summary>The ABI minor the loaded libddb reports in its capabilities (2: the build key, DroneDB 2.2.0).</summary>
        public int AbiMinor { get; set; } = 2;

        public UpgradeRescanStartupTask Task(params IUpgradeRescanStep[] steps)
        {
            var manager = new Mock<IDdbManager>();
            manager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()))
                .Returns<string, Guid>((_, internalRef) => Ddbs[internalRef].Object);

            var jobs = new Mock<IBackgroundJobsProcessor>();
            // The rebuilds of the entries whose key changed run on the background rebuild queue
            jobs.Setup(j => j.EnqueueIndexed<Registry.Web.Services.Jobs.DdbJobs>(
                    It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<IndexPayload>()))
                .Callback<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>, IndexPayload>(
                    (_, payload) => Queued.Add(payload.Path!))
                .Returns("job");

            var service = new IndexRescanService(Context, manager.Object, Mock.Of<ISystemTaskSubmitter>(),
                new RescanRebuild(Mock.Of<IDatasetCacheInvalidator>(), NullLogger<RescanRebuild>.Instance),
                NullLogger<IndexRescanService>.Instance);
            return new UpgradeRescanStartupTask(Context, service, steps.Length == 0 ? [new BuildKeyRescanStep()] : steps,
                manager.Object, TestJobs.Scheduler(jobs.Object), new RuntimeWithAbiMinor(AbiMinor),
                Microsoft.Extensions.Options.Options.Create(new StorageOptions { StoragePath = _storage }),
                new Registry.Adapters.FileSystem(), NullLogger<UpgradeRescanStartupTask>.Instance);
        }

        public int RescansOf(string org, string ds) =>
            Ddbs[Refs[$"{org}/{ds}"]].Invocations.Count(i => i.Method.Name == nameof(IDDB.RescanIndex));

        public UpgradeRescanState? State =>
            File.Exists(StatePath) ? JsonSerializer.Deserialize<UpgradeRescanState>(File.ReadAllText(StatePath),
                StateJson) : null;

        /// <summary>The progress of a step in the state file, null when it has none.</summary>
        public UpgradeRescanStepState? Progress(string stepId = BuildKeyRescanStep.StepId) =>
            State?.Steps?.GetValueOrDefault(stepId);

        public void WriteState(string json)
        {
            Directory.CreateDirectory(_storage);
            File.WriteAllText(StatePath, json);
        }

        public void Dispose()
        {
            Context.Dispose();
            if (Directory.Exists(_storage))
                Directory.Delete(_storage, true);
        }
    }

    [Test]
    public async Task TheJob_RescansEveryDatasetOnce_RebuildsOnlyTheChangedKeys_AndRecordsIt()
    {
        using var fx = new JobFixture(("org-a", "one"), ("org-a", "two"), ("org-b", "three"));

        await fx.Task().ExecuteAsync(CancellationToken.None);

        foreach (var (org, ds) in new[] { ("org-a", "one"), ("org-a", "two"), ("org-b", "three") })
            fx.RescansOf(org, ds).ShouldBe(1);
        fx.Queued.Order().ShouldBe(["one/geo.ply", "three/geo.ply", "two/geo.ply"]);
        fx.Progress()!.Completed.ShouldBeTrue();
        fx.Progress()!.Datasets.Count.ShouldBe(3);
        fx.Progress()!.Datasets.ShouldBe([.. fx.Refs.Values.Select(r => r.ToString("N")).Order(StringComparer.Ordinal)]);
        // The file is written in the shape with step ids only
        using (var written = JsonDocument.Parse(File.ReadAllText(fx.StatePath)))
            written.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["steps"]);

        // A later start does nothing: a dataset is rescanned once
        await fx.Task().ExecuteAsync(CancellationToken.None);
        foreach (var (org, ds) in new[] { ("org-a", "one"), ("org-a", "two"), ("org-b", "three") })
            fx.RescansOf(org, ds).ShouldBe(1);
        fx.Queued.Count.ShouldBe(3);
    }

    // §8.3: when a dataset fails, the job logs it, goes on with the others and tries it again at the next start
    [Test]
    public async Task TheJob_ADatasetThatFails_DoesNotStopTheOthers_AndIsTriedAgainAtTheNextStart()
    {
        using var fx = new JobFixture(("org", "bad"), ("org", "good"));
        var bad = fx.Ddbs[fx.Refs["org/bad"]];
        bad.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Throws(new DdbException("database is locked"));

        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "good").ShouldBe(1);
        fx.Progress()!.Completed.ShouldBeFalse("a dataset failed: the next start goes through them again");
        fx.Progress()!.Datasets.ShouldBe([fx.Refs["org/good"].ToString("N")]);

        // The next start rescans the one that failed, and not the one that is done
        bad.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([Rescanned("bad/geo.ply", true)]);
        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "good").ShouldBe(1);
        fx.RescansOf("org", "bad").ShouldBe(2);
        fx.Progress()!.Completed.ShouldBeTrue();
        fx.Progress()!.Datasets.Count.ShouldBe(2);
    }

    // D-050: the vector footprint rescan of U-209 is one more step, and the datasets are still read once
    [Test]
    public async Task TheJob_AnotherStep_AddsItsTypesToTheOneRescanOfEachDataset()
    {
        using var fx = new JobFixture(("org", "ds"));

        await fx.Task(new BuildKeyRescanStep(), new ExtraStep(EntryType.Vector, EntryType.Model)).ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "ds").ShouldBe(1);
        fx.Ddbs.Values.Single().Verify(d => d.RescanIndex(
            It.Is<RescanOptions?>(o => o!.Types == "model,gaussiansplat,vector")), Times.Once);
    }

    // FR-SHR-03, D-050: the Registry registers the build-key step and the vector footprint step, and one job reads
    // each dataset once for both: the vector entries are read again so that their footprints become WGS 84
    [Test]
    public void TheVectorFootprintStep_IsRegisteredWithTheBuildKeyStep_AndNamesTheVectorEntries()
    {
        using var services = new ServiceCollection().AddRegistryWebStartup().BuildServiceProvider();

        var steps = services.GetServices<IUpgradeRescanStep>().ToArray();

        steps.Select(s => s.Id).ShouldBe(["build-key", "vector-footprints"]);
        steps.Single(s => s.Id == "vector-footprints").EntryTypes.ShouldBe([EntryType.Vector]);
        UpgradeRescanSteps.FilterOf(steps).ToString().ShouldContain("vector");
    }

    [Test]
    public async Task TheJob_WithTheVectorFootprintStep_RescansTheVectorEntriesOfEachDatasetOnce_AndRecordsIt()
    {
        using var fx = new JobFixture(("org", "one"), ("org", "two")) { AbiMinor = 3 };

        await fx.Task(new BuildKeyRescanStep(), new VectorFootprintRescanStep()).ExecuteAsync(CancellationToken.None);

        foreach (var ddb in fx.Ddbs.Values)
            ddb.Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "model,gaussiansplat,vector")), Times.Once);
        fx.Progress("vector-footprints")!.Completed.ShouldBeTrue();
        fx.Progress("vector-footprints")!.Datasets.ShouldBe(
            new[] { fx.RefOf("org/one"), fx.RefOf("org/two") }.Order(StringComparer.Ordinal));

        // A dataset rescanned for it is not rescanned again at the next start
        await fx.Task(new BuildKeyRescanStep(), new VectorFootprintRescanStep()).ExecuteAsync(CancellationToken.None);
        fx.RescansOf("org", "one").ShouldBe(1);
        fx.RescansOf("org", "two").ShouldBe(1);
    }

    // A Registry that already ran the build-key rescan reads the vector entries once more at its first start of this release
    [Test]
    public async Task TheJob_AfterTheBuildKeyStepWasDone_RescansOnlyTheVectorEntries()
    {
        using var fx = new JobFixture(("org", "ds")) { AbiMinor = 3 };
        fx.Ddbs.Values.Single().Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([
            new EntrySummary { Path = "geo.ply", Type = EntryType.GaussianSplat, Hash = Hash },
            new EntrySummary { Path = "roads.fgb", Type = EntryType.Vector, Hash = Hash }]);
        await fx.Task(new BuildKeyRescanStep()).ExecuteAsync(CancellationToken.None);

        await fx.Task(new BuildKeyRescanStep(), new VectorFootprintRescanStep()).ExecuteAsync(CancellationToken.None);

        fx.Ddbs.Values.Single().Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "vector")), Times.Once);
    }

    // FR-SHR-03: libddb 2.2 does not have the WGS 84 footprint fix (it comes with ABI 2.3). The vector footprint step is
    // skipped without recording progress while the build-key step runs; at the first start on 2.3 it runs once
    [Test]
    public async Task TheJob_OnAbi22_SkipsTheVectorFootprintStepWithoutRecordingIt_AndRunsItOnceOnAbi23()
    {
        using var fx = new JobFixture(("org", "one"), ("org", "two")) { AbiMinor = 2 };
        var steps = new IUpgradeRescanStep[] { new BuildKeyRescanStep(), new VectorFootprintRescanStep() };
        foreach (var ddb in fx.Ddbs.Values)
            ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([
                new EntrySummary { Path = "geo.ply", Type = EntryType.GaussianSplat, Hash = Hash },
                new EntrySummary { Path = "roads.fgb", Type = EntryType.Vector, Hash = Hash }]);

        await fx.Task(steps).ExecuteAsync(CancellationToken.None);

        foreach (var ddb in fx.Ddbs.Values)
            ddb.Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "model,gaussiansplat")), Times.Once);
        fx.Progress("build-key")!.Completed.ShouldBeTrue();
        fx.State!.Steps!.ContainsKey("vector-footprints").ShouldBeFalse("a skipped step records nothing");
        fx.RescansOf("org", "one").ShouldBe(1);

        // The same start again on 2.2: nothing to do for the vector step, still nothing recorded
        await fx.Task(steps).ExecuteAsync(CancellationToken.None);
        fx.RescansOf("org", "one").ShouldBe(1);
        fx.State!.Steps!.ContainsKey("vector-footprints").ShouldBeFalse();

        // The first start on 2.3: the vector entries are read, once
        fx.AbiMinor = 3;
        await fx.Task(steps).ExecuteAsync(CancellationToken.None);
        foreach (var ddb in fx.Ddbs.Values)
            ddb.Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "vector")), Times.Once);
        fx.Progress("vector-footprints")!.Completed.ShouldBeTrue();
        fx.Progress("vector-footprints")!.Datasets.ShouldBe(
            new[] { fx.RefOf("org/one"), fx.RefOf("org/two") }.Order(StringComparer.Ordinal));

        await fx.Task(steps).ExecuteAsync(CancellationToken.None);
        fx.RescansOf("org", "one").ShouldBe(2);
    }

    [Test]
    public void TheVectorFootprintStep_NeedsAbi23_AndTheBuildKeyStepAbi22()
    {
        new VectorFootprintRescanStep().RequiredAbiMinor.ShouldBe(3);
        new BuildKeyRescanStep().RequiredAbiMinor.ShouldBe(2);
    }

    [Test]
    public async Task TheJob_WithoutDatasets_IsCompleteAtOnce()
    {
        using var fx = new JobFixture();

        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.Progress()!.Completed.ShouldBeTrue("a fresh installation has nothing to upgrade, and the datasets it creates later need no rescan");
    }

    [Test]
    public async Task TheJob_AStopRequest_StopsBetweenDatasets_AndKeepsWhatIsDone()
    {
        using var fx = new JobFixture(("org", "one"), ("org", "two"));
        using var stop = new CancellationTokenSource();
        fx.Ddbs[fx.Refs["org/one"]].Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>()))
            .Returns(() =>
            {
                stop.Cancel();
                return [Rescanned("one/geo.ply", true)];
            });

        await Should.ThrowAsync<OperationCanceledException>(() => fx.Task().ExecuteAsync(stop.Token));

        fx.RescansOf("org", "two").ShouldBe(0);
        fx.Progress()!.Completed.ShouldBeFalse();
        fx.Progress()!.Datasets.ShouldBe([fx.Refs["org/one"].ToString("N")]);
    }

    // The capabilities of a libddb 2.1 (ABI 2.1, before the build key): the job reads no dataset, records nothing and
    // runs at the first start with ABI 2.2
    [Test]
    public async Task TheJob_ALibddbWhoseCapabilitiesPredateTheBuildKey_RescansNothing_AndRunsWithAbi22()
    {
        using var fx = new JobFixture(("org", "a"), ("org", "b"));
        fx.AbiMinor = 1;

        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "a").ShouldBe(0);
        fx.RescansOf("org", "b").ShouldBe(0);
        fx.Queued.ShouldBeEmpty();
        fx.State.ShouldBeNull("nothing is recorded");

        fx.AbiMinor = 2;
        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "a").ShouldBe(1);
        fx.RescansOf("org", "b").ShouldBe(1);
        fx.Progress()!.Completed.ShouldBeTrue();
    }

    /// <summary>The recorded capabilities of libddb with another ABI minor.</summary>
    private sealed class RuntimeWithAbiMinor(int minor) : Registry.Adapters.Testing.FakeDdbRuntime
    {
        public override DdbCapabilities Capabilities { get; } = WithMinor(minor);

        private static DdbCapabilities WithMinor(int minor)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(
                Registry.Adapters.Testing.ContractFixture.Load("capabilities").Output.ToJsonString())!;
            json["abi"]!["minor"] = minor;
            return DdbCapabilities.Parse(json.ToJsonString());
        }
    }

    // §8.3 with the pinned libddb from before the build key: nothing is told apart, so nothing is recorded and the
    // job runs again at the first start with a libddb that has the key
    [Test]
    public async Task TheJob_ALibddbWithoutTheBuildKey_RecordsNothing_AndRunsWhenOneWithTheKeyArrives()
    {
        using var fx = new JobFixture(("org", "a-empty"), ("org", "b"), ("org", "c"));
        var empty = fx.Ddbs[fx.Refs["org/a-empty"]];
        empty.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([]);
        var b = fx.Ddbs[fx.Refs["org/b"]];
        b.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([OlderLibddb("b/geo.ply"), OlderLibddb("b/root.ply")]);
        var c = fx.Ddbs[fx.Refs["org/c"]];
        c.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([OlderLibddb("c/geo.ply")]);

        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "b").ShouldBe(1);
        fx.RescansOf("org", "c").ShouldBe(0, "the job stops at the first dataset that shows the libddb predates the key");
        fx.Queued.ShouldBeEmpty();
        (fx.Progress()?.Completed ?? false).ShouldBeFalse();
        (fx.Progress()?.Datasets ?? []).ShouldBeEmpty("not even the dataset without models or splats is recorded");

        // The libddb with the key: every dataset is rescanned and the step completes
        b.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([Rescanned("b/geo.ply", true), Rescanned("b/root.ply", false)]);
        c.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([Rescanned("c/geo.ply", true)]);
        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "b").ShouldBe(2);
        fx.RescansOf("org", "c").ShouldBe(1);
        fx.Queued.Order().ShouldBe(["b/geo.ply", "c/geo.ply"]);
        fx.Progress()!.Completed.ShouldBeTrue();
        fx.Progress()!.Datasets.ShouldBe(new[] { fx.RefOf("org/a-empty"), fx.RefOf("org/b"), fx.RefOf("org/c") }.Order(StringComparer.Ordinal));
    }

    // D-050: the step of a later release (U-209's vector footprints) runs for every dataset after the build-key
    // step is complete, and the build-key step does not run again
    [Test]
    public async Task TheJob_AStepRegisteredLater_RunsForEveryDataset_AndTheStepDoneDoesNotRunAgain()
    {
        using var fx = new JobFixture(("org", "one"), ("org", "two"));
        foreach (var ddb in fx.Ddbs.Values)
            ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([
                new EntrySummary { Path = "geo.ply", Type = EntryType.GaussianSplat, Hash = Hash },
                new EntrySummary { Path = "roads.fgb", Type = EntryType.Vector, Hash = Hash }]);

        await fx.Task(new BuildKeyRescanStep()).ExecuteAsync(CancellationToken.None);
        fx.Progress()!.Completed.ShouldBeTrue();
        fx.Queued.Count.ShouldBe(2);

        await fx.Task(new BuildKeyRescanStep(), new ExtraStep(EntryType.Vector)).ExecuteAsync(CancellationToken.None);

        foreach (var ddb in fx.Ddbs.Values)
        {
            ddb.Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "model,gaussiansplat")), Times.Once);
            ddb.Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "vector")), Times.Once);
        }
        fx.Progress("extra")!.Completed.ShouldBeTrue();
        fx.Progress("extra")!.Datasets.ShouldBe(new[] { fx.RefOf("org/one"), fx.RefOf("org/two") }.Order(StringComparer.Ordinal));
        fx.Progress()!.Completed.ShouldBeTrue();

        // Both done: a later start reads nothing
        await fx.Task(new BuildKeyRescanStep(), new ExtraStep(EntryType.Vector)).ExecuteAsync(CancellationToken.None);
        fx.RescansOf("org", "one").ShouldBe(2);
        fx.RescansOf("org", "two").ShouldBe(2);
    }

    // A state file of the first shape (no step ids) recorded the build-key step: its datasets are not rescanned for
    // it again, and a dataset gets the types of the steps not yet done for it in one rescan
    [Test]
    public async Task TheJob_AStateFileWithoutStepIds_IsTheProgressOfTheBuildKeyStep()
    {
        using var fx = new JobFixture(("org", "one"), ("org", "two"));
        foreach (var ddb in fx.Ddbs.Values)
            ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([
                new EntrySummary { Path = "geo.ply", Type = EntryType.GaussianSplat, Hash = Hash },
                new EntrySummary { Path = "roads.fgb", Type = EntryType.Vector, Hash = Hash }]);
        fx.WriteState($$"""{"completed": false, "datasets": ["{{fx.RefOf("org/one")}}"]}""");

        await fx.Task(new BuildKeyRescanStep(), new ExtraStep(EntryType.Vector)).ExecuteAsync(CancellationToken.None);

        fx.Ddbs[fx.Refs["org/one"]].Verify(d => d.RescanIndex(It.IsAny<RescanOptions?>()), Times.Once);
        fx.Ddbs[fx.Refs["org/one"]].Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "vector")), Times.Once);
        fx.Ddbs[fx.Refs["org/two"]].Verify(d => d.RescanIndex(It.IsAny<RescanOptions?>()), Times.Once);
        fx.Ddbs[fx.Refs["org/two"]].Verify(d => d.RescanIndex(It.Is<RescanOptions?>(o => o!.Types == "model,gaussiansplat,vector")), Times.Once);
        fx.Progress()!.Completed.ShouldBeTrue();
        fx.Progress("extra")!.Completed.ShouldBeTrue();
        fx.State!.Datasets.ShouldBeNull();

        // A complete file of the first shape: the build-key step does not run at all
        using var done = new JobFixture(("org", "one"));
        var firstShape = $$"""{"completed": true, "datasets": ["{{done.RefOf("org/one")}}"]}""";
        done.WriteState(firstShape);
        await done.Task().ExecuteAsync(CancellationToken.None);
        done.RescansOf("org", "one").ShouldBe(0);
        File.ReadAllText(done.StatePath).ShouldBe(firstShape, "nothing to do, nothing written");
    }

    // A dataset with an entry the rescan could not read is not done: the next start tries it again
    [Test]
    public async Task TheJob_ADatasetWithAnUnreadableEntry_IsNotRecorded_AndIsTriedAgainAtTheNextStart()
    {
        using var fx = new JobFixture(("org", "bad"), ("org", "good"));
        var bad = fx.Ddbs[fx.Refs["org/bad"]];
        bad.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>()))
            .Returns([Rescanned("bad/geo.ply", true), Rescanned("bad/locked.ply", false, success: false)]);

        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.Progress()!.Completed.ShouldBeFalse();
        fx.Progress()!.Datasets.ShouldBe([fx.RefOf("org/good")]);

        bad.Setup(d => d.RescanIndex(It.IsAny<RescanOptions?>())).Returns([Rescanned("bad/geo.ply", true), Rescanned("bad/locked.ply", false)]);
        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "bad").ShouldBe(2);
        fx.RescansOf("org", "good").ShouldBe(1);
        fx.Progress()!.Completed.ShouldBeTrue();
    }

    // Instances on a shared storage: the one that holds the lock runs the job, another skips it until its next start
    [Test]
    public async Task TheJob_WhileAnotherInstanceHoldsTheLock_IsSkipped_AndRunsAtTheNextStart()
    {
        using var fx = new JobFixture(("org", "ds"));
        Directory.CreateDirectory(Path.GetDirectoryName(fx.LockPath)!);

        using (new FileStream(fx.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "ds").ShouldBe(0);
        fx.State.ShouldBeNull();

        await fx.Task().ExecuteAsync(CancellationToken.None);

        fx.RescansOf("org", "ds").ShouldBe(1);
        fx.Progress()!.Completed.ShouldBeTrue();
    }
}
