using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// The statistics of a built COG on the real library (<c>DDBBuildRasterStatistics</c>): a COG without them (one built
/// before libddb wrote them) gets them, the <c>raster.cog</c> artifact says whether it has them and those of the uploaded
/// file, the value info of an entry (<c>DDBGetRasterValueInfoEx2</c>) is that of its file, and the formulas of a raster
/// come with its raster info (<c>DDBGetRasterInfo</c>) without its statistics.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~RasterStatisticsContractTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class RasterStatisticsContractTests : TestBase
{
    private const string BaseTestFolder = nameof(RasterStatisticsContractTests);
    private static readonly string GeoRasterUrl = TestData.Url("testdata/raster/byte.tif");

    private static readonly NativeDdbWrapper Wrapper = new();

    private string _geoRaster = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        new NativeDdbRuntime().RegisterProcess();
        _geoRaster = TestDownloadCache.GetOrDownload(GeoRasterUrl, Path.Combine(Path.GetTempPath(), BaseTestFolder));
    }

    [OneTimeTearDown]
    public void DeleteDownloads() => TestDownloadCache.Evict(_geoRaster);

    /// <summary>A dataset with <c>byte.tif</c>, indexed and not built.</summary>
    private string CreateDataset(TestArea area)
    {
        var root = Path.Combine(area.TestFolder, "dataset");
        Directory.CreateDirectory(root);
        Wrapper.Init(root);
        File.Copy(_geoRaster, Path.Combine(root, "byte.tif"));
        Wrapper.Add(root, [Path.Combine(root, "byte.tif")]);
        return root;
    }

    private static BuildArtifact Cog(string root) =>
        Wrapper.GetBuildArtifacts(root, ["byte.tif"]).Single().Build!.Artifacts
            .Single(a => a.Role == ArtifactRoles.RasterCog);

    [Test]
    public void ACogWithoutStatistics_GetsThem_AndItsArtifactSaysSo()
    {
        using var area = new TestArea(nameof(ACogWithoutStatistics_GetsThem_AndItsArtifactSaysSo));
        var root = CreateDataset(area);

        Wrapper.BuildRasterStatistics(root, "byte.tif", null, null, CancellationToken.None)
            .ShouldBe(RasterStatisticsStatus.NotBuilt);

        Wrapper.Build(root, "byte.tif");
        Cog(root).Statistics.ShouldBe(true, "the build writes them");

        var build = Wrapper.GetBuildArtifacts(root, ["byte.tif"]).Single().Build!;
        File.Delete(Path.Combine(build.Root!, Cog(root).Path) + ".stats.json");
        Cog(root).Statistics.ShouldBe(false, "a COG built before libddb wrote them");

        var fractions = new System.Collections.Generic.List<double>();
        Wrapper.BuildRasterStatistics(root, "byte.tif", BuildOptions.Capped(2), (f, _) => fractions.Add(f),
            CancellationToken.None).ShouldBe(RasterStatisticsStatus.Computed);
        Cog(root).Statistics.ShouldBe(true);
        fractions.ShouldNotBeEmpty();
        Wrapper.BuildRasterStatistics(root, "byte.tif", null, null, CancellationToken.None)
            .ShouldBe(RasterStatisticsStatus.Present);
    }

    [Test]
    public void TheValueInfoOfAnEntry_IsThatOfItsFile_WithTheStatisticsItsBuildKept()
    {
        using var area = new TestArea(nameof(TheValueInfoOfAnEntry_IsThatOfItsFile_WithTheStatisticsItsBuildKept));
        var root = CreateDataset(area);
        var file = Path.Combine(root, "byte.tif");
        var ofTheFile = Wrapper.GetRasterValueInfo(file, null);

        // Before a build there are no statistics of the build: the file is read
        Wrapper.GetEntryRasterValueInfo(root, "byte.tif", null).ShouldBe(ofTheFile);

        Wrapper.Build(root, "byte.tif");
        Cog(root).SourceStatistics.ShouldBe(true, "the build keeps the statistics of the uploaded file");
        Wrapper.GetEntryRasterValueInfo(root, "byte.tif", null).ShouldBe(ofTheFile);

        var build = Wrapper.GetBuildArtifacts(root, ["byte.tif"]).Single().Build!;
        File.Delete(Path.Combine(build.Root!, Cog(root).Path) + ".source.stats.json");
        Cog(root).SourceStatistics.ShouldBe(false, "a COG built before libddb kept them");
        Cog(root).Statistics.ShouldBe(true);
        Wrapper.GetEntryRasterValueInfo(root, "byte.tif", null).ShouldBe(ofTheFile);

        Wrapper.BuildRasterStatistics(root, "byte.tif", null, null, CancellationToken.None)
            .ShouldBe(RasterStatisticsStatus.Computed, "the statistics of the uploaded file were missing");
        Cog(root).SourceStatistics.ShouldBe(true);
        Wrapper.BuildRasterStatistics(root, "byte.tif", null, null, CancellationToken.None)
            .ShouldBe(RasterStatisticsStatus.Present);

        Should.Throw<DdbNotFoundException>(() => Wrapper.GetEntryRasterValueInfo(root, "missing.tif", null));
    }

    [Test]
    public void TheRasterInfo_ListsTheFormulasOfTheMetadata()
    {
        var info = System.Text.Json.Nodes.JsonNode.Parse(Wrapper.GetRasterInfo(_geoRaster))!;
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(Wrapper.GetRasterMetadata(_geoRaster))!;
        foreach (var key in new[] { "algorithms", "colormaps", "autoBands" })
            info[key]!.ToJsonString().ShouldBe(metadata[key]!.ToJsonString(), key);
    }
}
