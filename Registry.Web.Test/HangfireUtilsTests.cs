#nullable enable
using System;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
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

    [SetUp]
    public void SetUp()
    {
        _sink = new CollectingSink();
        _savedLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    [TearDown]
    public void TearDown() => Log.Logger = _savedLogger;

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
            m.Contains("Build failed: DdbException: Cannot open raster [InvalidOperationException]"));
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
    public void Describe_IncludesTypeNameMessageAndInnerType()
    {
        var line = HangfireUtils.Describe(
            new DdbException("GDAL failed to open", new System.Net.WebException("502")));
        line.ShouldBe("DdbException: GDAL failed to open [WebException]");
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
}
