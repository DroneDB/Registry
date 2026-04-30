using NUnit.Framework;
using Registry.Web.Services.Hub;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class HubVersionComparerTest
{
    [TestCase(null, "2.0.0", true, TestName = "OnDisk null -> upgrade")]
    [TestCase("", "2.0.0", true, TestName = "OnDisk empty -> upgrade")]
    [TestCase("   ", "2.0.0", true, TestName = "OnDisk whitespace -> upgrade")]
    [TestCase("garbage", "2.0.0", true, TestName = "OnDisk unparseable -> upgrade")]
    [TestCase("1.9.9", "2.0.0", true, TestName = "Older patch -> upgrade")]
    [TestCase("1.0.0", "2.0.0", true, TestName = "Older major -> upgrade")]
    [TestCase("2.0.0", "2.0.1", true, TestName = "Older patch component -> upgrade")]
    [TestCase("2.0.0", "2.0.0", false, TestName = "Same version -> keep")]
    [TestCase("2.1.0", "2.0.0", false, TestName = "Newer on disk -> keep")]
    [TestCase("3.0.0", "2.0.0", false, TestName = "Way newer on disk -> keep")]
    [TestCase("2.0.0-beta", "2.0.0", false, TestName = "Pre-release suffix ignored on equal core")]
    [TestCase("1.0.0-beta", "2.0.0", true, TestName = "Older with pre-release suffix -> upgrade")]
    [TestCase("2", "2.0.0", false, TestName = "Single component padded -> equal")]
    [TestCase("1.5", "2.0.0", true, TestName = "Two components padded older -> upgrade")]
    public void ShouldUpgrade_Cases(string onDisk, string embedded, bool expected)
    {
        HubVersionComparer.ShouldUpgrade(onDisk, embedded).ShouldBe(expected);
    }

    [Test]
    public void ShouldUpgrade_EmbeddedNullOrEmpty_ReturnsFalse()
    {
        HubVersionComparer.ShouldUpgrade("1.0.0", null).ShouldBeFalse();
        HubVersionComparer.ShouldUpgrade("1.0.0", "").ShouldBeFalse();
        HubVersionComparer.ShouldUpgrade("1.0.0", "garbage").ShouldBeFalse();
    }

    [Test]
    public void TryParse_ValidSemver_Succeeds()
    {
        HubVersionComparer.TryParse("1.2.3", out var v).ShouldBeTrue();
        v.Major.ShouldBe(1);
        v.Minor.ShouldBe(2);
        v.Build.ShouldBe(3);
    }

    [Test]
    public void TryParse_StripsPreReleaseAndBuild()
    {
        HubVersionComparer.TryParse("1.2.3-rc.1+abc", out var v).ShouldBeTrue();
        v.Major.ShouldBe(1);
        v.Minor.ShouldBe(2);
        v.Build.ShouldBe(3);
    }

    [Test]
    public void TryParse_PadsMissingComponents()
    {
        HubVersionComparer.TryParse("1", out var v1).ShouldBeTrue();
        v1.Build.ShouldBe(0);

        HubVersionComparer.TryParse("1.2", out var v2).ShouldBeTrue();
        v2.Build.ShouldBe(0);
    }

    [Test]
    public void TryParse_Invalid_ReturnsFalse()
    {
        HubVersionComparer.TryParse(null, out _).ShouldBeFalse();
        HubVersionComparer.TryParse("", out _).ShouldBeFalse();
        HubVersionComparer.TryParse("garbage", out _).ShouldBeFalse();
        HubVersionComparer.TryParse("a.b.c", out _).ShouldBeFalse();
    }
}
