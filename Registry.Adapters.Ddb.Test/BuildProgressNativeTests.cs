using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// FR-PRC-42 on the real libddb: the build of a raster reports its progress through <c>DDBBuildEx2</c> as
/// ddb.h describes it: the entry type first with no fraction, then the phases of the COG build, "publishing", and the
/// fraction 1 with no phase at the end; the fractions never go down.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~BuildProgressNativeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class BuildProgressNativeTests : TestBase
{
    private static readonly NativeDdbWrapper Wrapper = new();
    private string _folder = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        new NativeDdbRuntime().RegisterProcess();
    }

    [SetUp]
    public void CreateDataset()
    {
        _folder = Path.Combine(Path.GetTempPath(), nameof(BuildProgressNativeTests) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        Wrapper.Init(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "utm.tif"), Convert.FromBase64String(RasterValuesNativeTests.UtmTiff));
        Wrapper.Add(_folder, Path.Combine(_folder, "utm.tif"));
    }

    [TearDown]
    public void DeleteDataset() => Directory.Delete(_folder, true);

    [Test]
    public void TheBuildOfARaster_ReportsItsPhasesAndAFractionThatNeverGoesDown()
    {
        var reports = new List<(double Fraction, string? Phase)>();

        Wrapper.Build(_folder, "utm.tif", null, true, false, null, (fraction, phase) => reports.Add((fraction, phase)));

        reports.ShouldNotBeEmpty();
        reports[0].ShouldBe((-1d, "georaster"));
        reports[^1].ShouldBe((1d, (string?)null));
        // The UTM raster is warped to the tile grid: the phases of a COG build, in this order (a 20 x 20 raster has no
        // overview level, so it has no "overviews" phase)
        var phases = reports.Select(r => r.Phase).Where(p => p != null).Distinct().ToList();
        phases.ShouldBe(new List<string?> { "georaster", "warping", "compressing", "publishing" });
        var measured = reports.Select(r => r.Fraction).SkipWhile(f => f < 0).ToList();
        measured.ShouldAllBe(f => f >= 0 && f <= 1);
        measured.Zip(measured.Skip(1)).ShouldAllBe(pair => pair.Second >= pair.First);
        reports.Take(reports.Count - 1).ShouldAllBe(r => r.Fraction < 1, "no report before the last is 1");
    }

    // The build is the same build: its artifacts are there afterwards
    [Test]
    public void TheBuildWithProgress_BuildsTheEntry()
    {
        Wrapper.Build(_folder, "utm.tif", null, true, false, null, (_, _) => { });

        var artifacts = Wrapper.GetBuildArtifacts(_folder, ["utm.tif"]);
        artifacts.Length.ShouldBe(1);
        artifacts[0].Build.ShouldNotBeNull();
        artifacts[0].Build!.State.ShouldBe(Registry.Ports.DroneDB.BuildState.Complete);
    }
}
