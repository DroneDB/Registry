#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using Hangfire;
using Hangfire.Server;
using Hangfire.InMemory;
using Hangfire.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Build wrappers must record WHY a build failed in the
/// job log (catch-all + rethrow), so the Task History "View log" shows something
/// better than ["Running build"]. The ring-buffer plumbing (BuildLogCaptureFilter /
/// CreateJobWriteLine) is pre-existing and untouched; here the fallback write path
/// (context == null -> Serilog) is captured to assert the failure line and the rethrow.
/// </summary>
[TestFixture]
public class HangfireUtilsTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<string> Messages { get; } = [];

        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }

    private CollectingSink _sink = null!;
    private Serilog.ILogger _savedLogger = null!;
    private JobStorageScope _storageScope = null!;

    [SetUp]
    public void SetUp()
    {
        _storageScope = new JobStorageScope(new InMemoryStorage());
        _sink = new CollectingSink();
        _savedLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        Log.Logger = _savedLogger;
        _storageScope.Dispose();
    }

    private static Mock<IDDB> MockDdb()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/datasets/test-org/test-ds");
        return ddb;
    }

    [Test]
    public void BuildWrapper_GenericException_RethrowsAndWritesFailureLine()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.Build("ortho.tif", null, false)).Throws(new Exception("tile corrupt"));

        var ex = Should.Throw<Exception>(() =>
            HangfireUtils.BuildWrapper(ddb.Object, "ortho.tif", false, null));

        ex.Message.ShouldBe("tile corrupt");
        _sink.Messages.ShouldContain(m => m.StartsWith("In BuildWrapper("));
        _sink.Messages.ShouldContain(m => m.Contains("Build failed: Exception: tile corrupt"));
        // The Done line must NOT appear when the build threw.
        _sink.Messages.ShouldNotContain(m => m.Contains("Done build"));
    }

    [Test]
    public void BuildWrapper_DdbException_RethrowsAndWritesTypedFailureLine()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.Build("cloud.laz", null, false))
            .Throws(new DdbException("Cannot open raster", new InvalidOperationException("GDAL failed")));

        Should.Throw<DdbException>(() =>
            HangfireUtils.BuildWrapper(ddb.Object, "cloud.laz", false, null));

        _sink.Messages.ShouldContain(m =>
            m.Contains("Build failed: DdbException: Cannot open raster [InvalidOperationException: GDAL failed]"));
    }

    [Test]
    public void BuildWrapper_BuildInProgress_SkipsWithoutRethrowAndWithoutFailedLine()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.Build("in-bld.tif", null, false))
            .Throws(new DdbBuildInProgressException("locked by pid 42"));

        Should.NotThrow(() => HangfireUtils.BuildWrapper(ddb.Object, "in-bld.tif", false, null));

        _sink.Messages.ShouldContain(m =>
            m.Contains("Build lock currently held by another process (locked by pid 42); skipping"));
        _sink.Messages.ShouldContain(m => m.Contains("Done build (skipped: lock held elsewhere)"));
        _sink.Messages.ShouldNotContain(m => m.Contains("Build failed:"));
    }

    [Test]
    public void BuildWrapper_Success_WritesDoneLineWithoutFailedLine()
    {
        var ddb = MockDdb();

        Should.NotThrow(() => HangfireUtils.BuildWrapper(ddb.Object, "ortho.tif", false, null));

        ddb.Verify(x => x.Build("ortho.tif", null, false), Times.Once);
        _sink.Messages.ShouldContain(m => m.Contains("Done build"));
        _sink.Messages.ShouldNotContain(m => m.Contains("Build failed:"));
    }

    // ---- BuildPendingWrapper native fallback (context == null -> no org/ds/services -> ddb.BuildPending) ----

    [Test]
    public void BuildPendingWrapper_GenericException_RethrowsAndWritesTypedFailureLine()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.BuildPending(null, false)).Throws(new DdbException("pdal crashed"));

        Should.Throw<DdbException>(() =>
            HangfireUtils.BuildPendingWrapper(ddb.Object, null));

        _sink.Messages.ShouldContain(m => m.Contains("In BuildPendingWrapper("));
        _sink.Messages.ShouldContain(m => m.Contains("Build pending failed: DdbException: pdal crashed"));
        _sink.Messages.ShouldNotContain(m => m.Contains("Done build pending"));
    }

    [Test]
    public void BuildPendingWrapper_BuildInProgress_SkipsWithoutRethrow()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.BuildPending(null, false)).Throws(new DdbBuildInProgressException("locked"));

        Should.NotThrow(() => HangfireUtils.BuildPendingWrapper(ddb.Object, null));

        _sink.Messages.ShouldContain(m => m.Contains("Done build pending (skipped: lock held elsewhere)"));
        _sink.Messages.ShouldNotContain(m => m.Contains("Build pending failed:"));
    }

    // ---- Describe (shared formatting for both wrappers) ----

    [Test]
    public void Describe_Null_ReturnsUnknownError()
    {
        HangfireUtils.Describe(null!).ShouldBe("Unknown error");
    }

    [Test]
    public void Describe_CollapsesNewlinesIntoSingleDisplayLine()
    {
        var line = HangfireUtils.Describe(new Exception("first line\r\nsecond\nline"));
        line.ShouldBe("Exception: first line second line");
    }

    [Test]
    public void Describe_IncludesTypeNameMessageAndInnerMessage()
    {
        var line = HangfireUtils.Describe(
            new DdbException("GDAL failed to open", new System.Net.WebException("502")));
        line.ShouldBe("DdbException: GDAL failed to open [WebException: 502]");
    }

    [Test]
    public void Describe_SurfacesTheNativeErrorBehindAdapterWrappers()
    {
        // Ddb.Build wraps every native failure; the cause is the inner message.
        var line = HangfireUtils.Describe(new InvalidOperationException("Cannot build 'a.dwg' from ddb '[dataset]'",
            new DdbException("DWG support needs dwg2dxf (LibreDWG)\nset DDB_DWG2DXF_PATH")));
        line.ShouldBe("InvalidOperationException: Cannot build 'a.dwg' from ddb '[dataset]' " +
                      "[DdbException: DWG support needs dwg2dxf (LibreDWG) set DDB_DWG2DXF_PATH]");
    }

    [Test]
    public void Describe_InnerWithoutMessage_KeepsInnerType()
    {
        HangfireUtils.Describe(new Exception("outer", new TimeoutException("")))
            .ShouldBe("Exception: outer [TimeoutException]");
    }

    [Test]
    public void Describe_TruncatesLongInnerMessagesTo300Chars()
    {
        var line = HangfireUtils.Describe(new Exception("outer", new Exception(new string('y', 400))));
        line.ShouldBe("Exception: outer [Exception: " + new string('y', 300) + "...]");
    }

    [Test]
    public void Describe_TruncatesLongMessagesTo500Chars()
    {
        var line = HangfireUtils.Describe(new Exception(new string('x', 600)));
        line.Length.ShouldBe("Exception: ".Length + 500 + "...".Length);
        line.ShouldEndWith("...");
    }

    [Test]
    public void Describe_EmptyMessage_KeepsTypeName()
    {
        HangfireUtils.Describe(new InvalidOperationException("")).ShouldBe("InvalidOperationException: ");
    }

    // ---- Dataset-path scrubbing of the anonymous-visible job log tail ----

    [Test]
    public void BuildWrapper_DdbBusyException_RethrowsForRetryAndScrubsDatasetPathEverywhere()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.Build("ortho.tif", null, false))
            .Throws(new DdbBusyException(
                "sqlite3_busy: cannot write /data/datasets/test-org/test-ds/.ddb/build/raster1/cog/cog.tif"));

        // The exception must propagate so AutomaticRetry (OnlyOn DdbBusy/DdbBuildInProgress) applies.
        Should.Throw<DdbBusyException>(() =>
            HangfireUtils.BuildWrapper(ddb.Object, "ortho.tif", false, null));

        _sink.Messages.ShouldContain(m => m.StartsWith("In BuildWrapper('[dataset]'"));
        _sink.Messages.ShouldContain(m => m.Contains("Build failed: DdbBusyException:"));
        _sink.Messages.ShouldContain(m => m.Contains("[dataset]/.ddb/build/raster1/cog/cog.tif"));
        // The absolute storage root must never reach the ring buffer / Serilog fallback.
        _sink.Messages.ShouldNotContain(m => m.Contains("/data/datasets"));
        _sink.Messages.ShouldNotContain(m => m.Contains("Done build"));
    }

    [Test]
    public void BuildWrapper_BuildInProgressSkipLine_ScrubsBackslashPathVariant()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.Build("locked.tif", null, false))
            .Throws(new DdbBuildInProgressException(
                @"lock file \data\datasets\test-org\test-ds\.ddb\build.lock held"));

        Should.NotThrow(() => HangfireUtils.BuildWrapper(ddb.Object, "locked.tif", false, null));

        _sink.Messages.ShouldContain(m =>
            m.Contains("lock file [dataset]\\.ddb\\build.lock held"));
        _sink.Messages.ShouldNotContain(m => m.Contains(@"\data\datasets"));
    }

    [Test]
    public void BuildPendingWrapper_Failure_ScrubsDatasetPathFromEntryAndFailureLines()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.BuildPending(null, false))
            .Throws(new Exception("boom at /data/datasets/test-org/test-ds/laz/file.laz"));

        Should.Throw<Exception>(() => HangfireUtils.BuildPendingWrapper(ddb.Object, null));

        _sink.Messages.ShouldContain(m => m.StartsWith("In BuildPendingWrapper('[dataset]'"));
        _sink.Messages.ShouldContain(m =>
            m.Contains("Build pending failed: Exception: boom at [dataset]/laz/file.laz"));
        _sink.Messages.ShouldNotContain(m => m.Contains("/data/datasets"));
    }

    [Test]
    public void CleanupWrapper_ScrubsDatasetPath()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.Cleanup()).Returns(new DdbCleanupResult());

        HangfireUtils.CleanupWrapper(ddb.Object, null);

        _sink.Messages.ShouldContain(m => m.StartsWith("In CleanupWrapper('[dataset]'"));
        _sink.Messages.ShouldNotContain(m => m.Contains("/data/datasets"));
    }

    // ---- BuildPendingWrapper enqueue path: one indexed BuildWrapper per due pending entry ----

    private const string Org = "test-org";
    private const string Ds = "test-ds";
    private const long Now = 1_800_000_000;

    private sealed record EnqueuedBuild(string Method, IDDB Ddb, string Path, bool Force, IndexPayload Meta);

    // Captures every EnqueueIndexed(Expression<Action>, IndexPayload) call, evaluating the
    // BuildWrapper arguments the way Hangfire would serialize them.
    private static Mock<IBackgroundJobsProcessor> MockJobs(List<EnqueuedBuild> enqueued)
    {
        var jobs = new Mock<IBackgroundJobsProcessor>(MockBehavior.Strict);
        jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Returns<Expression<Action>, IndexPayload>((expr, meta) =>
            {
                var call = (MethodCallExpression)expr.Body;
                object? Eval(int i) => Expression.Lambda(call.Arguments[i]).Compile().DynamicInvoke();
                enqueued.Add(new EnqueuedBuild(call.Method.Name, (IDDB)Eval(0)!, (string)Eval(1)!, (bool)Eval(2)!, meta));
                (Eval(3)).ShouldBeNull(); // PerformContext is injected by Hangfire at run time
                return $"job-{enqueued.Count}";
            });
        return jobs;
    }

    private static Mock<IJobIndexQuery> MockJobIndex(params JobIndex[] rows)
    {
        // Script real storage states as well as the display rows: dedup consults
        // authoritative Hangfire state after acquiring the dataset lock.
        var client = new BackgroundJobClient(JobStorage.Current);
        foreach (var row in rows)
            row.JobId = client.Create(Hangfire.Common.Job.FromExpression(() => HangfireUtils.DummyJob(null)),
                row.CurrentState == "Enqueued" ? new Hangfire.States.EnqueuedState() : new SnapshotState(row.CurrentState));
        var q = new Mock<IJobIndexQuery>();
        q.Setup(x => x.GetByOrgDsAsync(Org, Ds, 0, int.MaxValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);
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

    private static PendingBuildInfo Pending(string path, long secondsAgo, string hash = "h") =>
        new() { Path = path, Hash = hash, MissingDependencies = ["x.mtl"], LastAttempt = Now - secondsAgo };

    private static JobIndex Job(string path, string state, string toolId = "build") =>
        new() { JobId = Guid.NewGuid().ToString(), OrgSlug = Org, DsSlug = Ds, Path = path, ToolId = toolId, CurrentState = state };

    [Test]
    public void BuildPendingWrapper_EnqueuesOneBuildPerDueEntry_SkipsBackoffEmptyPathAndActive()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.GetPendingBuildInfo()).Returns([
            Pending("model.obj", 61, "h-root"),
            Pending("sub/model.obj", 60, "h-sub"),     // exactly at the 60 s boundary: due
            Pending("recent.obj", 59, "h-recent"),     // inside the backoff
            Pending("", 600, "h-empty"),
            Pending("   ", 600, "h-blank"),
            Pending("busy.obj", 600, "h-busy"),        // has an active build job
            new PendingBuildInfo { Path = "never.obj", Hash = "h-never", LastAttempt = 0 }
        ]);
        var enqueued = new List<EnqueuedBuild>();
        var jobs = MockJobs(enqueued);
        var index = MockJobIndex(
            Job("busy.obj", "Processing"),
            Job("model.obj", "Failed"),                 // terminal: does not block
            Job("sub/model.obj", "Succeeded"),          // terminal: does not block
            Job("never.obj", "Enqueued", "raster-export")); // other tool: does not block

        HangfireUtils.BuildPendingWrapper(ddb.Object, null, jobs.Object, index.Object, Org, Ds, () => Now);

        enqueued.Select(e => e.Path).ShouldBe(["model.obj", "sub/model.obj", "never.obj"]);
        enqueued.ShouldAllBe(e => e.Method == nameof(HangfireUtils.BuildWrapper) && !e.Force && e.Ddb == ddb.Object);

        var root = enqueued[0].Meta;
        root.ShouldBe(new IndexPayload(Org, Ds, "h-root", MagicStrings.AutoBuildServiceUserId, null, "model.obj"));
        root.ToolId.ShouldBe("build");
        enqueued[1].Meta.Path.ShouldBe("sub/model.obj");
        enqueued[1].Meta.Hash.ShouldBe("h-sub");

        ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _sink.Messages.ShouldContain(m => m.Contains(
            "Pending builds: 7; enqueued 3; skipped 4 (1 attempted less than 60s ago, 1 with an active build job, 2 without a path)"));
        _sink.Messages.ShouldContain(m => m.Contains("Enqueued build of 'sub/model.obj' (job job-2)"));
        _sink.Messages.ShouldContain(m => m.Contains("Done build pending"));
    }

    [Test]
    public void BuildPendingWrapper_DuplicatePendingPath_EnqueuedOnce()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.GetPendingBuildInfo()).Returns([Pending("a.obj", 120, "h1"), Pending("a.obj", 120, "h1")]);
        var enqueued = new List<EnqueuedBuild>();

        HangfireUtils.BuildPendingWrapper(ddb.Object, null, MockJobs(enqueued).Object, MockJobIndex().Object, Org, Ds,
            () => Now);

        enqueued.Count.ShouldBe(1);
    }

    [Test]
    public void BuildPendingWrapper_NothingDue_EnqueuesNothingAndSkipsJobIndexQuery()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.GetPendingBuildInfo()).Returns([Pending("a.obj", 10), Pending("", 600)]);
        var enqueued = new List<EnqueuedBuild>();
        var index = MockJobIndex();

        HangfireUtils.BuildPendingWrapper(ddb.Object, null, MockJobs(enqueued).Object, index.Object, Org, Ds, () => Now);

        enqueued.ShouldBeEmpty();
        index.Verify(x => x.GetByOrgDsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _sink.Messages.ShouldContain(m => m.Contains("Pending builds: 2; enqueued 0; skipped 2"));
        _sink.Messages.ShouldContain(m => m.Contains("Done build pending"));
    }

    [Test]
    public void BuildPendingWrapper_GetPendingBuildInfoThrows_RethrowsWithScrubbedFailureLine()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.GetPendingBuildInfo())
            .Throws(new InvalidOperationException("Cannot call GetPendingBuildInfo from ddb '/data/datasets/test-org/test-ds'"));

        Should.Throw<InvalidOperationException>(() => HangfireUtils.BuildPendingWrapper(ddb.Object, null,
            MockJobs([]).Object, MockJobIndex().Object, Org, Ds, () => Now));

        _sink.Messages.ShouldContain(m => m.Contains("Build pending failed: InvalidOperationException:"));
        _sink.Messages.ShouldNotContain(m => m.Contains("/data/datasets"));
        _sink.Messages.ShouldNotContain(m => m.Contains("Done build pending"));
        ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    // ---- Legacy signature (what Hangfire runs): resolution from the PerformContext ----

    private sealed class StubActivator(IReadOnlyDictionary<Type, object> services) : JobActivator
    {
        public int Disposed;

        public override JobActivatorScope BeginScope(JobActivatorContext context) => new Scope(this, services);

        private sealed class Scope(StubActivator owner, IReadOnlyDictionary<Type, object> services) : JobActivatorScope
        {
            public override object Resolve(Type type) =>
                services.TryGetValue(type, out var s) ? s : throw new InvalidOperationException($"No {type.Name}");

            public override void DisposeScope() => owner.Disposed++;
        }
    }

    private static (PerformContext Context, LogRingBuffer Buffer) ContextWithParams(string? org, string? ds)
    {
        var conn = new Mock<IStorageConnection>();
        conn.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(Mock.Of<IDisposable>());
        conn.Setup(x => x.GetJobParameter("42", JobParamKeys.OrgSlug)).Returns(org!);
        conn.Setup(x => x.GetJobParameter("42", JobParamKeys.DsSlug)).Returns(ds!);
        var job = new BackgroundJob("42", Hangfire.Common.Job.FromExpression(() => HangfireUtils.DummyJob(null!)),
            DateTime.UtcNow);
        var context = new PerformContext(null, conn.Object, job, new Mock<IJobCancellationToken>().Object);
        var buffer = new LogRingBuffer();
        context.Items[BuildLogCaptureFilter.BufferKey] = buffer;
        return (context, buffer);
    }

    private static void WithActivator(JobActivator activator, Action body)
    {
        var saved = JobActivator.Current;
        JobActivator.Current = activator;
        try { body(); }
        finally { JobActivator.Current = saved; }
    }

    [Test]
    public void BuildPendingWrapper_FromJobContext_ResolvesServicesAndEnqueuesInsteadOfNativeBuildPending()
    {
        var ddb = MockDdb();
        ddb.Setup(x => x.GetPendingBuildInfo()).Returns([
            new PendingBuildInfo { Path = "sub/model.obj", Hash = "h1", LastAttempt = 0 }
        ]);
        var enqueued = new List<EnqueuedBuild>();
        var activator = new StubActivator(new Dictionary<Type, object>
        {
            [typeof(IBackgroundJobsProcessor)] = MockJobs(enqueued).Object,
            [typeof(IJobIndexQuery)] = MockJobIndex().Object
        });
        var (context, buffer) = ContextWithParams(Org, Ds);

        WithActivator(activator, () => HangfireUtils.BuildPendingWrapper(ddb.Object, context));

        enqueued.Count.ShouldBe(1);
        enqueued[0].Meta.ShouldBe(new IndexPayload(Org, Ds, "h1", MagicStrings.AutoBuildServiceUserId, null,
            "sub/model.obj"));
        ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        activator.Disposed.ShouldBe(1);
        buffer.ToJson().ShouldContain("Done build pending");
        buffer.ToJson().ShouldNotContain("falling back");
    }

    [Test]
    public void BuildPendingWrapper_JobWithoutOrgDsParams_FallsBackToNativeBuildPending()
    {
        var ddb = MockDdb();
        var activator = new StubActivator(new Dictionary<Type, object>());
        var (context, buffer) = ContextWithParams(null, Ds);

        WithActivator(activator, () => HangfireUtils.BuildPendingWrapper(ddb.Object, context));

        ddb.Verify(x => x.BuildPending(null, false), Times.Once);
        ddb.Verify(x => x.GetPendingBuildInfo(), Times.Never);
        buffer.ToJson().ShouldContain("job has no organization/dataset parameters; falling back to native build pending");
        buffer.ToJson().ShouldContain("Done build pending");
    }

    [Test]
    public void BuildPendingWrapper_ServicesUnresolvable_FallsBackToNativeBuildPendingAndDisposesScope()
    {
        var ddb = MockDdb();
        var activator = new StubActivator(new Dictionary<Type, object>()); // Resolve throws
        var (context, buffer) = ContextWithParams(Org, Ds);

        WithActivator(activator, () => HangfireUtils.BuildPendingWrapper(ddb.Object, context));

        ddb.Verify(x => x.BuildPending(null, false), Times.Once);
        activator.Disposed.ShouldBe(1);
        buffer.ToJson().ShouldContain("cannot resolve job services");
    }

    [Test]
    public void BuildPendingWrapper_NullContext_FallsBackToNativeBuildPending()
    {
        var ddb = MockDdb();

        HangfireUtils.BuildPendingWrapper(ddb.Object, null);

        ddb.Verify(x => x.BuildPending(null, false), Times.Once);
        _sink.Messages.ShouldContain(m => m.Contains("no job context; falling back to native build pending"));
        _sink.Messages.ShouldContain(m => m.Contains("Done build pending"));
    }
}
