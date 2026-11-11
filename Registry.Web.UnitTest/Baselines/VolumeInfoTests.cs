using System;
using System.IO;
using NUnit.Framework;
using Registry.Common;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>Path.GetPathRoot</c> returns <c>/</c> on Linux, so the
/// disk-headroom checks (<c>CommonUtils.GetStorageInfo</c>, <c>ExtractionBudget.GetAvailableDiskBytes</c>,
/// <c>ArchiveExtractTool.EnsureDiskSpace</c>) measured the container root and not the dataset volume
/// (prod keeps datasets on a separate RAID5 mount). After the fix all three use
/// <see cref="VolumeInfo.For(string)"/>, which picks the mount point that is the longest
/// path-segment-aware prefix of the full path (OS case rules).
/// <para>Covered with an injected mount table; one smoke test runs against the real one.</para>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~VolumeInfoTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class VolumeInfoTests
{
    private static readonly string[] Mounts = ["/", "/data", "/data/datasets"];

    private static string? MountOf(string path, params string[] mounts) => VolumeInfo.FindMountRoot(path, mounts);

    private static VolumeInfo? For(string path, params string[] mounts) =>
        VolumeInfo.For(path, () => mounts, root => new VolumeInfo(1000, 100 + root.Length));

    [Test]
    public void DatasetPath_ResolvesToTheDatasetMount_NotTheRoot()
    {
        var volume = For("/data/datasets/org/ds", Mounts);

        volume.ShouldNotBeNull();
        VolumeInfo.FindMountRoot("/data/datasets/org/ds", Mounts).ShouldBe("/data/datasets");
        volume.AvailableFreeBytes.ShouldBe(100 + "/data/datasets".Length); // measured on that mount
    }

    [Test]
    public void MountTableOrder_DoesNotMatter()
    {
        MountOf("/data/datasets/org/ds", "/data/datasets", "/", "/data").ShouldBe("/data/datasets");
    }

    [Test]
    public void SiblingWithCommonPrefix_IsNotUnderTheMount()
    {
        MountOf("/datafoo/x", Mounts).ShouldBe("/");
    }

    [Test]
    public void PathEqualToAMountPoint_ResolvesToThatMount()
    {
        MountOf("/data", Mounts).ShouldBe("/data");
        MountOf("/data/", Mounts).ShouldBe("/data");
    }

    [Test]
    public void MountRootWithTrailingSeparator_StillMatches()
    {
        MountOf("/data/x", "/", "/data/").ShouldBe("/data/");
    }

    [Test]
    public void RelativeSegments_AreNormalisedFirst()
    {
        MountOf("/data/datasets/../other/x", Mounts).ShouldBe("/data");
    }

    [Test]
    public void Case_FollowsTheOs()
    {
        var expected = OperatingSystem.IsWindows() ? "/data/datasets" : "/";
        MountOf("/DATA/DATASETS/org/ds", Mounts).ShouldBe(expected);
    }

    [Test]
    public void NoMountContainsThePath_ReturnsNull()
    {
        For("/srv/x", "/data", "/data/datasets").ShouldBeNull();
    }

    [Test]
    public void UnmeasurableMount_ReturnsNull()
    {
        VolumeInfo.For("/data/datasets/x", () => Mounts, _ => null).ShouldBeNull();
    }

    [Test]
    public void RealMountTable_TempPath_IsMeasured()
    {
        var volume = VolumeInfo.For(Path.GetTempPath());

        volume.ShouldNotBeNull();
        volume.TotalBytes.ShouldBeGreaterThan(0);
        volume.AvailableFreeBytes.ShouldBeInRange(0, volume.TotalBytes);
    }
}
