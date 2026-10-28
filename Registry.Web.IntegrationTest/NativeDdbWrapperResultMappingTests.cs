#nullable disable
using System;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test;

/// <summary>
/// Unit coverage for the DdbResult → typed-exception mapping, centralized in
/// <see cref="DdbResultMapper.Map"/>. The assertions target the exception
/// TYPES (the systemic property: transient contention must surface as the typed transient
/// exception, not a generic <c>DdbException</c>), and do not pin the native last-error
/// message text.
/// </summary>
[TestFixture]
[Category(TestCategories.Native)]
public class NativeDdbWrapperResultMappingTests
{
    [Test]
    public void Success_HasNoException()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DdbResultMapper.Map(DdbResult.Success, "op", null, null));

    [Test]
    public void Busy_MapsToDdbBusyException()
        => DdbResultMapper.Map(DdbResult.Busy, "op", null, null).ShouldBeOfType<DdbBusyException>();

    [Test]
    public void BuildInProgress_MapsToDdbBuildInProgressException()
        => DdbResultMapper.Map(DdbResult.BuildInProgress, "op", null, null).ShouldBeOfType<DdbBuildInProgressException>();

    [Test]
    public void Canceled_MapsToDdbCanceledException()
        => DdbResultMapper.Map(DdbResult.Canceled, "op", null, null).ShouldBeOfType<DdbCanceledException>();

    [Test]
    public void UnknownResult_MapsToBaseDdbException()
        // exactly the base type: not any of the subclasses
        => DdbResultMapper.Map((DdbResult)99, "op", null, null).ShouldBeOfType<DdbException>();
}
