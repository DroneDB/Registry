using System;
using System.IO;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class DdbBuildBusyPassthroughTests
{
    // NativeDdbWrapper.Build cannot be unit-tested without the native lib, so the busyness is
    // faked at the mockable seam one level up: IDdbWrapper.Build throwing/touching nothing.
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ddb_build_busy_").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }

    private static (DDB Ddb, Mock<IDdbWrapper> Wrapper) CreateDdb()
    {
        var wrapper = new Mock<IDdbWrapper>();
        using var tmp = new TempDir();
        return (new DDB(tmp.Path, wrapper.Object), wrapper);
    }

    private static void SetupBuildThrow(Mock<IDdbWrapper> wrapper, Exception ex) =>
        wrapper.Setup(w => w.Build(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<bool>()))
            .Throws(ex);

    [Test]
    public void Build_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        SetupBuildThrow(wrapper, new DdbBusyException("locked"));

        var ex = Assert.Throws<DdbBusyException>(() => ddb.Build("src.tif"));
        ex.Message.ShouldBe("locked");
    }

    [Test]
    public void BuildAll_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        SetupBuildThrow(wrapper, new DdbBusyException("locked"));

        Assert.Throws<DdbBusyException>(() => ddb.BuildAll());
    }

    [Test]
    public void BuildPending_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        SetupBuildThrow(wrapper, new DdbBusyException("locked"));

        Assert.Throws<DdbBusyException>(() => ddb.BuildPending());
    }

    [Test]
    public void Build_DdbBuildInProgressException_StillPassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        SetupBuildThrow(wrapper, new DdbBuildInProgressException("building"));

        Assert.Throws<DdbBuildInProgressException>(() => ddb.BuildAll());
    }

    [Test]
    public void Build_PlainDdbException_StillWrappedInInvalidOperationException()
    {
        var (ddb, wrapper) = CreateDdb();
        SetupBuildThrow(wrapper, new DdbException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(() => ddb.Build("src.tif"));
        ex.InnerException.ShouldBeOfType<DdbException>();
    }

    // Review round 2 systemic fix: the catch-filter on every DDB wrapper used to bury the
    // transient typed result (newly thrown by the underlying helper) under an IOE, so the
    // API 503 + Retry-After path never fired for these operations.
    [Test]
    public void Cleanup_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.Cleanup(It.IsAny<string>())).Throws(new DdbBusyException("locked"));

        var ex = Assert.Throws<DdbBusyException>(() => ddb.Cleanup());
        ex.Message.ShouldBe("locked");
    }

    [Test]
    public void Remove_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.Remove(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbBusyException("locked"));

        var ex = Assert.Throws<DdbBusyException>(() => ddb.Remove("a/input.xlsx"));
        ex.Message.ShouldBe("locked");
    }

    [Test]
    public void Move_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.MoveEntry(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbBusyException("locked"));

        var ex = Assert.Throws<DdbBusyException>(() => ddb.Move("a", "b"));
        ex.Message.ShouldBe("locked");
    }

    [Test]
    public void GenerateThumbnail_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()))
            .Throws(new DdbBusyException("locked"));

        var ex = Assert.Throws<DdbBusyException>(() => ddb.GenerateThumbnail("a/input.tif", 64));
        ex.Message.ShouldBe("locked");
    }

    [Test]
    public void RescanIndex_DdbBusyException_PassesThroughUnwrapped()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.RescanIndex(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Throws(new DdbBusyException("locked"));

        var ex = Assert.Throws<DdbBusyException>(() => ddb.RescanIndex());
        ex.Message.ShouldBe("locked");
    }

    [Test]
    public void Cleanup_PlainDdbException_StillWrappedInInvalidOperationException()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.Cleanup(It.IsAny<string>())).Throws(new DdbException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(() => ddb.Cleanup());
        ex.InnerException.ShouldBeOfType<DdbException>();
    }

    [Test]
    public void RescanIndex_PlainDdbException_StillWrappedInInvalidOperationException()
    {
        var (ddb, wrapper) = CreateDdb();
        wrapper.Setup(w => w.RescanIndex(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Throws(new DdbException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(() => ddb.RescanIndex());
        ex.InnerException.ShouldBeOfType<DdbException>();
    }
}
