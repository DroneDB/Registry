#nullable enable
using System;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// XI-02, RQ-08: every libddb result code maps to one typed exception carrying libddb's message
/// and reason (<see cref="DdbResultMapper.Map"/>, pure: no libddb).
/// </summary>
[TestFixture]
public class DdbResultMapperTests
{
    [TestCase(DdbResult.Busy, typeof(DdbBusyException))]
    [TestCase(DdbResult.BuildInProgress, typeof(DdbBuildInProgressException))]
    [TestCase(DdbResult.Canceled, typeof(DdbCanceledException))]
    [TestCase(DdbResult.NotFound, typeof(DdbNotFoundException))]
    [TestCase(DdbResult.InvalidArgs, typeof(DdbInvalidArgumentsException))]
    [TestCase(DdbResult.Retryable, typeof(DdbRetryableException))]
    [TestCase(DdbResult.Shutdown, typeof(DdbShutdownException))]
    [TestCase(DdbResult.Exception, typeof(DdbException))]
    [TestCase(DdbResult.BuildDependencyMissing, typeof(DdbException))]
    [TestCase((DdbResult)99, typeof(DdbException))]
    public void EveryCode_MapsToItsType_WithTheNativeMessage(DdbResult result, Type expected)
    {
        var ex = DdbResultMapper.Map(result, "DDBX", "native message", "");

        ex.GetType().ShouldBe(expected);
        ex.Message.ShouldBe("native message");
    }

    [Test]
    public void Canceled_IsAnOperationCanceledException_NotAFailure()
    {
        var ex = DdbResultMapper.Map(DdbResult.Canceled, "DDBBuild", "canceled", "");

        ex.ShouldBeAssignableTo<OperationCanceledException>();
        ex.ShouldNotBeAssignableTo<DdbException>();
    }

    [Test]
    public void Retryable_CarriesItsReason()
    {
        var ex = DdbResultMapper.Map(DdbResult.Retryable, "DDBCopyBuildArtifacts", "disk full", "noSpaceOnDevice")
            .ShouldBeOfType<DdbRetryableException>();

        ex.Reason.ShouldBe(DdbRetryableException.Reasons.NoSpaceOnDevice);
    }

    /// <summary>The exports older than ABI 1.1 (DDBBuild) keep DDBERR_EXCEPTION but set the reason of a transient failure.</summary>
    [TestCase("lowDiskSpace")]
    [TestCase("noSpaceOnDevice")]
    [TestCase("idleTimeout")]
    public void Exception_WithARetryableReason_IsRetryable(string reason)
    {
        DdbResultMapper.Map(DdbResult.Exception, "DDBBuild", "build failed", reason)
            .ShouldBeOfType<DdbRetryableException>().Reason.ShouldBe(reason);
    }

    /// <summary>ddb.h: reasons are only ever added; an unknown one is treated like none.</summary>
    [Test]
    public void Exception_WithAnUnknownReason_IsAPlainFailure()
    {
        DdbResultMapper.Map(DdbResult.Exception, "DDBBuild", "build failed", "someFutureReason")
            .GetType().ShouldBe(typeof(DdbException));
    }

    [TestCase(null)]
    [TestCase("")]
    public void NoMessage_NamesTheCallAndTheCode(string? message)
    {
        DdbResultMapper.Map(DdbResult.Busy, "DDBAdd", message, null).Message.ShouldBe("DDBAdd failed (Busy)");
    }

    [Test]
    public void Success_HasNoException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DdbResultMapper.Map(DdbResult.Success, "DDBAdd", "", ""));
    }
}
