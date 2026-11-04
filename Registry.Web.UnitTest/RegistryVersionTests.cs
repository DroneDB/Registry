using NUnit.Framework;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.VersionTests;

/// <summary>
/// The Registry version (GET /sys/version, the features) is always major.minor.patch, never a 4-part string ending
/// in ".0". It is built by <see cref="RegistryVersion"/>.
/// </summary>
[TestFixture]
public class RegistryVersionTests
{
    [Test]
    public void GetVersion_ReturnsThreePartSemver()
    {
        var version = RegistryVersion.Current;

        version.ShouldNotBeNullOrEmpty("the version should not be empty");

        var parts = version.Split('.');
        parts.Length.ShouldBe(3, $"Expected 3-part semver but got {parts.Length} parts: '{version}'");
        foreach (var part in parts)
            int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out _)
                .ShouldBeTrue($"'{part}' of '{version}' is not a number");
    }
}
