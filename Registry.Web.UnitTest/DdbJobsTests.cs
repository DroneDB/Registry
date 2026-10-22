#nullable enable
// The job log names the exception type: a plain Exception is one of the cases under test
#pragma warning disable CA2201
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The dataset jobs (<see cref="DdbJobs"/>) and the pending-build enqueue path
/// (<see cref="PendingBuildEnqueuer"/>): failures land in the job log tail (catch-all + rethrow), the
/// tail is scrubbed of the dataset folder, and a pending retry enqueues one indexed build per due
/// entry. Ported from the tests of the static HangfireUtils wrappers (RQ-09).
/// </summary>
[TestFixture]
public class DdbJobsTests
{
    private const string Org = "test-org";
    private const string Ds = "test-ds";
    private const long Now = 1_800_000_000;
    private static readonly Guid Ref = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private static readonly string Folder = $"/data/datasets/{Org}/{Ref}";

    private CapturingLogger<DdbJobs> _log = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IDDB> _ddb = null!;
    private JobStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _log = new CapturingLogger<DdbJobs>();
        _storage = new InMemoryStorage();
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(x => x.DatasetFolderPath).Returns(Folder);
        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(x => x.Open(Org, Ref)).Returns(() => _ddb.Object);
        var org = Org;
        var internalRef = Ref;
        _ddbManager.Setup(x => x.TryParseDatasetFolder(Folder, out org, out internalRef)).Returns(true);
    }

    private IEnumerable<string> Logged => _log.Entries.Select(e => e.Message);

    private DdbJobs Jobs(IBackgroundJobsProcessor? jobs = null, IJobIndexQuery? index = null) =>
        new(_ddbManager.Object, Pending(jobs, index), new FileSystem(), _log);

    private PendingBuildEnqueuer Pending(IBackgroundJobsProcessor? jobs = null, IJobIndexQuery? index = null) =>
        new(jobs ?? new Mock<IBackgroundJobsProcessor>(MockBehavior.Strict).Object, index ?? MockJobIndex().Object,
            _ddbManager.Object, _storage, new FakeTime(DateTimeOffset.FromUnixTimeSeconds(Now).UtcDateTime));

    // A job context with org/ds parameters (or not) and the build log tail BuildLogCaptureFilter attaches
    private static (PerformContext Context, LogRingBuffer Tail) Context(string? org = Org, string? ds = Ds)
    {
        var conn = new Mock<IStorageConnection>();
        conn.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(Mock.Of<IDisposable>());
        conn.Setup(x => x.GetJobParameter("42", JobParamKeys.OrgSlug)).Returns(org!);
        conn.Setup(x => x.GetJobParameter("42", JobParamKeys.DsSlug)).Returns(ds!);
        var job = new BackgroundJob("42", Hangfire.Common.Job.FromExpression<HealthCheckJob>(j => j.Ping(null)),
            DateTime.UtcNow);
        var context = new PerformContext(null, conn.Object, job, new Mock<IJobCancellationToken>().Object);
        var tail = new LogRingBuffer();
        context.Items[BuildLogCaptureFilter.BufferKey] = tail;
        return (context, tail);
    }

    // ---- Build ----

    [Test]
    public void Build_GenericException_RethrowsAndWritesFailureLine()
    {
        _ddb.Setup(x => x.Build("ortho.tif", null!, false)).Throws(new Exception("tile corrupt"));
        var (context, tail) = Context();

        var ex = Should.Throw<Exception>(() => Jobs().Build(Org, Ref, "ortho.tif", false, context));

        ex.Message.ShouldBe("tile corrupt");
        var lines = tail.Snapshot().Lines.Select(l => l.Msg).ToList();
        lines.ShouldContain(m => m.StartsWith("In Build([dataset], ortho.tif, False)"));
        lines.ShouldContain(m => m.Contains("Build failed: Exception: tile corrupt"));
        lines.ShouldNotContain(m => m.Contains("Done build"));
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Build job 42 failed for ortho.tif"));
    }

    [Test]
    public void Build_DdbException_RethrowsAndWritesTypedFailureLine()
    {
        _ddb.Setup(x => x.Build("cloud.laz", null!, false))
            .Throws(new DdbException("Cannot open raster", new InvalidOperationException("GDAL failed")));

        Should.Throw<DdbException>(() => Jobs().Build(Org, Ref, "cloud.laz", false, null));

        Logged.ShouldContain(m =>
            m.Contains("Build failed: DdbException: Cannot open raster [InvalidOperationException: GDAL failed]"));
    }

    [Test]
    public void Build_BuildInProgress_SkipsWithoutRethrowAndWithoutFailedLine()
    {
        _ddb.Setup(x => x.Build("in-bld.tif", null!, false)).Throws(new DdbBuildInProgressException("locked by pid 42"));

        Should.NotThrow(() => Jobs().Build(Org, Ref, "in-bld.tif", false, null));

        Logged.ShouldContain(m => m.Contains("Build lock currently held by another process (locked by pid 42); skipping"));
        Logged.ShouldContain(m => m.Contains("Done build (skipped: lock held elsewhere)"));
        Logged.ShouldNotContain(m => m.Contains("Build failed:"));
    }

    [Test]
    public void Build_Success_WritesDoneLine()
    {
        Should.NotThrow(() => Jobs().Build(Org, Ref, "ortho.tif", false, null));

        _ddb.Verify(x => x.Build("ortho.tif", null!, false), Times.Once);
        Logged.ShouldContain(m => m.Contains("Done build"));
        Logged.ShouldNotContain(m => m.Contains("Build failed:"));
    }

    [Test]
    public void Build_OpensTheDatasetWithoutCreatingIt()
    {
        Jobs().Build(Org, Ref, "ortho.tif", true, null);

        _ddbManager.Verify(x => x.Open(Org, Ref), Times.Once);
        _ddbManager.Verify(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        _ddb.Verify(x => x.Build("ortho.tif", null!, true), Times.Once);
    }

    [Test]
    public void Build_DdbBusyException_RethrowsForRetryAndScrubsDatasetPathFromTheTail()
    {
        _ddb.Setup(x => x.Build("ortho.tif", null!, false))
            .Throws(new DdbBusyException($"sqlite3_busy: cannot write {Folder}/.ddb/build/raster1/cog/cog.tif"));
        var (context, tail) = Context();

        Should.Throw<DdbBusyException>(() => Jobs().Build(Org, Ref, "ortho.tif", false, context));

        var lines = tail.Snapshot().Lines.Select(l => l.Msg).ToList();
        lines.ShouldContain(m => m.Contains("Build failed: DdbBusyException:"));
        lines.ShouldContain(m => m.Contains("[dataset]/.ddb/build/raster1/cog/cog.tif"));
        lines.ShouldNotContain(m => m.Contains("/data/datasets"));
        lines.ShouldNotContain(m => m.Contains("Done build"));
        // The server log's warning is scrubbed too
        _log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldAllBe(e => !e.Message.Contains("/data/datasets"));
    }

    [Test]
    public void Build_BuildInProgressSkipLine_ScrubsBackslashPathVariant()
    {
        _ddb.Setup(x => x.Build("locked.tif", null!, false))
            .Throws(new DdbBuildInProgressException(@"lock file \data\datasets\test-org\" + Ref + @"\.ddb\build.lock held"));
        var (context, tail) = Context();

        Should.NotThrow(() => Jobs().Build(Org, Ref, "locked.tif", false, context));

        var lines = tail.Snapshot().Lines.Select(l => l.Msg).ToList();
        lines.ShouldContain(m => m.Contains("lock file [dataset]\\.ddb\\build.lock held"));
        lines.ShouldNotContain(m => m.Contains(@"\data\datasets"));
    }

    [Test]
    public void Cleanup_ScrubsDatasetPath()
    {
        _ddb.Setup(x => x.Cleanup()).Returns(new DdbCleanupResult());
        var (context, tail) = Context();

        Jobs().Cleanup(Org, Ref, context);

        var lines = tail.Snapshot().Lines.Select(l => l.Msg).ToList();
        lines.ShouldContain(m => m.StartsWith("In Cleanup([dataset])"));
        lines.ShouldContain(m => m.Contains("Removed 0 entries and 0 build artifacts"));
        lines.ShouldNotContain(m => m.Contains("/data/datasets"));
    }

    // ---- BuildPending: native fallback (no org/ds) ----

    [Test]
    public void BuildPending_WithoutContext_NativeFallbackFailure_RethrowsAndWritesTypedFailureLine()
    {
        _ddb.Setup(x => x.BuildPending(null!, false)).Throws(new DdbException("pdal crashed"));

        Should.Throw<DdbException>(() => Jobs().BuildPending(Org, Ref, null));

        Logged.ShouldContain(m => m.Contains("In BuildPending("));
        Logged.ShouldContain(m => m.Contains("no job context; falling back to native build pending"));
        Logged.ShouldContain(m => m.Contains("Build pending failed: DdbException: pdal crashed"));
        Logged.ShouldNotContain(m => m.Contains("Done build pending"));
    }

    [Test]
    public void BuildPending_NativeFallback_BuildInProgress_SkipsWithoutRethrow()
    {
        _ddb.Setup(x => x.BuildPending(null!, false)).Throws(new DdbBuildInProgressException("locked"));

        Should.NotThrow(() => Jobs().BuildPending(Org, Ref, null));

        Logged.ShouldContain(m => m.Contains("Done build pending (skipped: lock held elsewhere)"));
        Logged.ShouldNotContain(m => m.Contains("Build pending failed:"));
    }

    [Test]
    public void BuildPending_NativeFallbackFailure_ScrubsDatasetPathFromTheTail()
    {
        var (context, tail) = Context(org: null);
        _ddb.Setup(x => x.BuildPending(null!, false)).Throws(new Exception($"boom at {Folder}/laz/file.laz"));

        Should.Throw<Exception>(() => Jobs().BuildPending(Org, Ref, context));

        var lines = tail.Snapshot().Lines.Select(l => l.Msg).ToList();
        lines.ShouldContain(m => m.StartsWith("In BuildPending([dataset])"));
        lines.ShouldContain(m => m.Contains("Build pending failed: Exception: boom at [dataset]/laz/file.laz"));
        lines.ShouldNotContain(m => m.Contains("/data/datasets"));
    }

    [Test]
    public void BuildPending_JobWithoutOrgDsParams_FallsBackToNativeBuildPending()
    {
        var (context, tail) = Context(org: null);

        Jobs().BuildPending(Org, Ref, context);

        _ddb.Verify(x => x.BuildPending(null!, false), Times.Once);
        _ddb.Verify(x => x.GetPendingBuildInfo(), Times.Never);
        var json = tail.ToJson();
        json.ShouldContain("job has no organization/dataset parameters; falling back to native build pending");
        json.ShouldContain("Done build pending");
    }

    // ---- BuildPending: one indexed Build per due pending entry ----

    private sealed record EnqueuedBuild(string Method, string OrgSlug, Guid InternalRef, string Path, bool Force,
        IndexPayload Meta);

    // Captures every EnqueueIndexed<DdbJobs> call, evaluating the arguments the way Hangfire serializes them
    private static Mock<IBackgroundJobsProcessor> MockJobs(List<EnqueuedBuild> enqueued)
    {
        var jobs = new Mock<IBackgroundJobsProcessor>(MockBehavior.Strict);
        jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Returns<Expression<Action<DdbJobs>>, IndexPayload>((expr, meta) =>
            {
                var call = (MethodCallExpression)expr.Body;
                object? Eval(int i) => Expression.Lambda(call.Arguments[i]).Compile().DynamicInvoke();
                enqueued.Add(new EnqueuedBuild(call.Method.Name, (string)Eval(0)!, (Guid)Eval(1)!, (string)Eval(2)!,
                    (bool)Eval(3)!, meta));
                Eval(4).ShouldBeNull(); // PerformContext is injected by Hangfire at run time
                return $"job-{enqueued.Count}";
            });
        return jobs;
    }

    private Mock<IJobIndexQuery> MockJobIndex(params JobIndex[] rows)
    {
        // Real storage states as well as the display rows: dedup consults the authoritative
        // Hangfire state after acquiring the dataset lock
        var client = new BackgroundJobClient(_storage);
        foreach (var row in rows)
            row.JobId = client.Create(Hangfire.Common.Job.FromExpression<HealthCheckJob>(j => j.Ping(null)),
                row.CurrentState == "Enqueued" ? new Hangfire.States.EnqueuedState() : new SnapshotState(row.CurrentState));
        var q = new Mock<IJobIndexQuery>();
        q.Setup(x => x.GetByOrgDsAsync(Org, Ds, 0, int.MaxValue, It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        return q;
    }

    private sealed class SnapshotState(string name) : Hangfire.States.IState
    {
        public string Name => name;
        public string Reason => "Test snapshot";
        public bool IsFinal => name is "Failed" or "Succeeded" or "Deleted";
        public bool IgnoreJobLoadException => false;
        public Dictionary<string, string> SerializeData() => [];
    }

    private static PendingBuildInfo PendingEntry(string path, long secondsAgo, string hash = "h") =>
        PendingMarkers.Of(path, hash, Now - secondsAgo, "x.mtl");

    private static JobIndex Row(string path, string state, string toolId = "build") =>
        new() { JobId = Guid.NewGuid().ToString(), OrgSlug = Org, DsSlug = Ds, Path = path, ToolId = toolId, CurrentState = state };

    private JobConsole Console() => JobConsole.For(_log, null, Folder);

    [Test]
    public void Pending_EnqueuesOneBuildPerDueEntry_SkipsBackoffEmptyPathAndActive()
    {
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([
            PendingEntry("model.obj", 61, "h-root"),
            PendingEntry("sub/model.obj", 60, "h-sub"),     // exactly at the 60 s boundary: due
            PendingEntry("recent.obj", 59, "h-recent"),     // inside the backoff
            PendingEntry("", 600, "h-empty"),
            PendingEntry("   ", 600, "h-blank"),
            PendingEntry("busy.obj", 600, "h-busy"),        // has an active build job
            PendingMarkers.Of("never.obj", "h-never")
        ]);
        var enqueued = new List<EnqueuedBuild>();
        var index = MockJobIndex(
            Row("busy.obj", "Processing"),
            Row("model.obj", "Failed"),                 // terminal: does not block
            Row("sub/model.obj", "Succeeded"),          // terminal: does not block
            Row("never.obj", "Enqueued", "raster-export")); // other tool: does not block

        Pending(MockJobs(enqueued).Object, index.Object).Run(_ddb.Object, Org, Ds, Console());

        enqueued.Select(e => e.Path).ShouldBe(["model.obj", "sub/model.obj", "never.obj"]);
        // The new job shape (D4): organization and InternalRef, never the DDB object
        enqueued.ShouldAllBe(e => e.Method == nameof(DdbJobs.Build) && e.OrgSlug == Org && e.InternalRef == Ref && !e.Force);

        var root = enqueued[0].Meta;
        root.ShouldBe(new IndexPayload(Org, Ds, "h-root", MagicStrings.AutoBuildServiceUserId, null, "model.obj"));
        root.ToolId.ShouldBe("build");
        enqueued[1].Meta.Hash.ShouldBe("h-sub");

        _ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        Logged.ShouldContain(m => m.Contains(
            "Pending builds: 7; enqueued 3; skipped 4 (1 not yet due, 1 with an active build job, 2 without a path)"));
        Logged.ShouldContain(m => m.Contains("Enqueued build of 'sub/model.obj' (job job-2)"));
        Logged.ShouldContain(m => m.Contains("Done build pending"));
    }

    [Test]
    public void Pending_DueIsLibddbsRetryAfter_NotARegistryBackoff()
    {
        // XI-04: libddb owns the retry rule and reports it as retryAfter; the enqueue path must follow
        // it whatever the time since lastAttempt, or a coordinator run and libddb's buildPending disagree
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([
            new PendingBuildInfo { Path = "short.obj", Hash = "h1", LastAttempt = Now - 5, RetryAfter = Now - 1 },
            new PendingBuildInfo { Path = "long.obj", Hash = "h2", LastAttempt = Now - 600, RetryAfter = Now + 1 },
            new PendingBuildInfo { Path = "now.obj", Hash = "h3", LastAttempt = Now - 600, RetryAfter = Now }
        ]);
        var enqueued = new List<EnqueuedBuild>();

        Pending(MockJobs(enqueued).Object).Run(_ddb.Object, Org, Ds, Console());

        enqueued.Select(e => e.Path).ShouldBe(["short.obj", "now.obj"]);
        Logged.ShouldContain(m => m.Contains("Pending builds: 3; enqueued 2; skipped 1 (1 not yet due"));
    }

    [Test]
    public void Pending_DuplicatePendingPath_EnqueuedOnce()
    {
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([PendingEntry("a.obj", 120, "h1"), PendingEntry("a.obj", 120, "h1")]);
        var enqueued = new List<EnqueuedBuild>();

        Pending(MockJobs(enqueued).Object).Run(_ddb.Object, Org, Ds, Console());

        enqueued.Count.ShouldBe(1);
    }

    [Test]
    public void Pending_NothingDue_EnqueuesNothingAndSkipsJobIndexQuery()
    {
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([PendingEntry("a.obj", 10), PendingEntry("", 600)]);
        var enqueued = new List<EnqueuedBuild>();
        var index = MockJobIndex();

        Pending(MockJobs(enqueued).Object, index.Object).Run(_ddb.Object, Org, Ds, Console());

        enqueued.ShouldBeEmpty();
        index.Verify(x => x.GetByOrgDsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Logged.ShouldContain(m => m.Contains("Pending builds: 2; enqueued 0; skipped 2"));
        Logged.ShouldContain(m => m.Contains("Done build pending"));
    }

    [Test]
    public void Pending_GetPendingBuildInfoThrows_RethrowsWithScrubbedFailureLine()
    {
        _ddb.Setup(x => x.GetPendingBuildInfo())
            .Throws(new InvalidOperationException($"Cannot call GetPendingBuildInfo from ddb '{Folder}'"));
        var (context, tail) = Context();

        Should.Throw<InvalidOperationException>(() =>
            Pending(MockJobs([]).Object).Run(_ddb.Object, Org, Ds, JobConsole.For(_log, context, Folder)));

        var lines = tail.Snapshot().Lines.Select(l => l.Msg).ToList();
        lines.ShouldContain(m => m.Contains("Build pending failed: InvalidOperationException:"));
        lines.ShouldNotContain(m => m.Contains("/data/datasets"));
        lines.ShouldNotContain(m => m.Contains("Done build pending"));
        _ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public void BuildPending_FromJobContext_EnqueuesInsteadOfNativeBuildPending()
    {
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([
            PendingMarkers.Of("sub/model.obj", "h1")
        ]);
        var enqueued = new List<EnqueuedBuild>();
        var (context, tail) = Context();

        Jobs(MockJobs(enqueued).Object).BuildPending(Org, Ref, context);

        enqueued.Count.ShouldBe(1);
        enqueued[0].Meta.ShouldBe(new IndexPayload(Org, Ds, "h1", MagicStrings.AutoBuildServiceUserId, null,
            "sub/model.obj"));
        _ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        tail.ToJson().ShouldContain("Done build pending");
        tail.ToJson().ShouldNotContain("falling back");
    }

    [Test]
    public void BuildPending_DatasetFolderOutsideTheDatasetsPath_FallsBackToNativeBuildPending()
    {
        var foreign = new Mock<IDDB>();
        foreign.SetupGet(x => x.DatasetFolderPath).Returns("/elsewhere/x");
        _ddbManager.Setup(x => x.Open(Org, Ref)).Returns(foreign.Object);
        var (context, tail) = Context();

        Jobs().BuildPending(Org, Ref, context);

        foreign.Verify(x => x.BuildPending(null!, false), Times.Once);
        tail.ToJson().ShouldContain("not under the datasets path; falling back to native build pending");
    }

    // ---- Text helpers ----

    [Test]
    public void Describe_Null_ReturnsUnknownError() => JobLogText.Describe(null).ShouldBe("Unknown error");

    [Test]
    public void Describe_CollapsesNewlinesIntoSingleDisplayLine() =>
        JobLogText.Describe(new Exception("first line\r\nsecond\nline")).ShouldBe("Exception: first line second line");

    [Test]
    public void Describe_IncludesTypeNameMessageAndInnerMessage() =>
        JobLogText.Describe(new DdbException("GDAL failed to open", new System.Net.WebException("502")))
            .ShouldBe("DdbException: GDAL failed to open [WebException: 502]");

    [Test]
    public void Describe_SurfacesTheNativeErrorBehindAdapterWrappers() =>
        JobLogText.Describe(new InvalidOperationException("Cannot build 'a.dwg' from ddb '[dataset]'",
                new DdbException("DWG support needs dwg2dxf (LibreDWG)\nset DDB_DWG2DXF_PATH")))
            .ShouldBe("InvalidOperationException: Cannot build 'a.dwg' from ddb '[dataset]' " +
                      "[DdbException: DWG support needs dwg2dxf (LibreDWG) set DDB_DWG2DXF_PATH]");

    [Test]
    public void Describe_InnerWithoutMessage_KeepsInnerType() =>
        JobLogText.Describe(new Exception("outer", new TimeoutException(""))).ShouldBe("Exception: outer [TimeoutException]");

    [Test]
    public void Describe_TruncatesLongInnerMessagesTo300Chars() =>
        JobLogText.Describe(new Exception("outer", new Exception(new string('y', 400))))
            .ShouldBe("Exception: outer [Exception: " + new string('y', 300) + "...]");

    [Test]
    public void Describe_TruncatesLongMessagesTo500Chars()
    {
        var line = JobLogText.Describe(new Exception(new string('x', 600)));
        line.Length.ShouldBe("Exception: ".Length + 500 + "...".Length);
        line.ShouldEndWith("...");
    }

    [Test]
    public void Describe_EmptyMessage_KeepsTypeName() =>
        JobLogText.Describe(new InvalidOperationException("")).ShouldBe("InvalidOperationException: ");
}
