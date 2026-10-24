#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Registry.Common;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 5 (Linux disk-space check): <c>Path.GetPathRoot</c> returns <c>/</c> on Linux, so the
/// disk-headroom checks (<c>CommonUtils.GetStorageInfo</c>, <c>ExtractionBudget.GetAvailableDiskBytes</c>,
/// <c>ArchiveExtractTool.EnsureDiskSpace</c>) measured the container root and not the dataset volume
/// (prod keeps datasets on a separate RAID5 mount). After the fix all three use
/// <see cref="VolumeInfo.For(string)"/>, which picks the mount point that is the longest
/// path-segment-aware prefix of the full path (OS case rules).
/// <para>Covered with an injected mount table; one smoke test runs against the real one.</para>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq02VolumeInfoTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq02VolumeInfoTests
{
    private static readonly string[] Mounts = ["/", "/data", "/data/datasets"];

    private static VolumeInfo? For(string path, params string[] mounts) =>
        VolumeInfo.For(path, () => mounts, root => new VolumeInfo(root, 1000, 100 + root.Length));

    [Test]
    public void DatasetPath_ResolvesToTheDatasetMount_NotTheRoot()
    {
        var volume = For("/data/datasets/org/ds", Mounts);

        volume.ShouldNotBeNull();
        volume.RootDirectory.ShouldBe("/data/datasets");
        volume.AvailableFreeBytes.ShouldBe(100 + "/data/datasets".Length); // measured on that mount
    }

    [Test]
    public void MountTableOrder_DoesNotMatter()
    {
        For("/data/datasets/org/ds", "/data/datasets", "/", "/data")!.RootDirectory.ShouldBe("/data/datasets");
    }

    [Test]
    public void SiblingWithCommonPrefix_IsNotUnderTheMount()
    {
        For("/datafoo/x", Mounts)!.RootDirectory.ShouldBe("/");
    }

    [Test]
    public void PathEqualToAMountPoint_ResolvesToThatMount()
    {
        For("/data", Mounts)!.RootDirectory.ShouldBe("/data");
        For("/data/", Mounts)!.RootDirectory.ShouldBe("/data");
    }

    [Test]
    public void MountRootWithTrailingSeparator_StillMatches()
    {
        For("/data/x", "/", "/data/")!.RootDirectory.ShouldBe("/data/");
    }

    [Test]
    public void RelativeSegments_AreNormalisedFirst()
    {
        For("/data/datasets/../other/x", Mounts)!.RootDirectory.ShouldBe("/data");
    }

    [Test]
    public void Case_FollowsTheOs()
    {
        var expected = OperatingSystem.IsWindows() ? "/data/datasets" : "/";
        For("/DATA/DATASETS/org/ds", Mounts)!.RootDirectory.ShouldBe(expected);
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
