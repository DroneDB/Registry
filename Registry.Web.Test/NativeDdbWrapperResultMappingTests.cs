using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Unit coverage for the DdbResult → typed-exception mapping, centralized in
/// <see cref="DdbResultMapper.ThrowForFinalResult"/>. The assertions target the exception
/// TYPES (the systemic property: transient contention must surface as the typed transient
/// exception, not a generic <c>DdbException</c>), and do not pin the native last-error
/// message text (it is an empty string whenever no native call has set a last error).
/// </summary>
[TestFixture]
public class NativeDdbWrapperResultMappingTests
{
    [Test]
    public void Success_DoesNotThrow()
        => Assert.That(() => DdbResultMapper.ThrowForFinalResult(DdbResult.Success, "op"), Throws.Nothing);

    [Test]
    public void Busy_MapsToDdbBusyException()
    {
        Assert.Throws<DdbBusyException>(
            () => DdbResultMapper.ThrowForFinalResult(DdbResult.Busy, "op"));
    }

    [Test]
    public void BuildInProgress_MapsToDdbBuildInProgressException()
    {
        Assert.Throws<DdbBuildInProgressException>(
            () => DdbResultMapper.ThrowForFinalResult(DdbResult.BuildInProgress, "op"));
    }

    [Test]
    public void Canceled_MapsToDdbCanceledException()
    {
        Assert.Throws<DdbCanceledException>(
            () => DdbResultMapper.ThrowForFinalResult(DdbResult.Canceled, "op"));
    }

    [Test]
    public void UnknownResult_MapsToBaseDdbException()
    {
        var unknown = (DdbResult)99;
        var ex = Assert.Throws<DdbException>(() => DdbResultMapper.ThrowForFinalResult(unknown, "op"));
        // type-specific base: not any of the subclasses
        ex.ShouldNotBeOfType<DdbBusyException>();
    }
}
