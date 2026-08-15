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
}
