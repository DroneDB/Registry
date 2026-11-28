using System.Text.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// The statistics members of the <c>raster.cog</c> artifact of libddb's build manifest, read with the contract of
/// libddb's output (<see cref="NativeJson.Options"/>, pure: no libddb): whether the COG has its statistics next to it
/// and whether it has those of the uploaded file. A libddb that does not say leaves them null, and a null member is
/// not written back.
/// </summary>
[TestFixture]
public class BuildArtifactJsonTests
{
    private static BuildArtifact Read(string json) =>
        JsonSerializer.Deserialize<BuildArtifact>(json, NativeJson.Options).ShouldNotBeNull();

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void TheStatisticsMembers_AreRead_AndWrittenBack(bool statistics, bool sourceStatistics)
    {
        var json = $$"""
            {"role":"raster.cog","path":"cog/cog.tif","kind":"file","mediaType":"image/tiff",
             "statistics":{{(statistics ? "true" : "false")}},"sourceStatistics":{{(sourceStatistics ? "true" : "false")}}}
            """;

        var artifact = Read(json);

        artifact.Statistics.ShouldBe(statistics);
        artifact.SourceStatistics.ShouldBe(sourceStatistics);
        Read(JsonSerializer.Serialize(artifact, NativeJson.Options)).SourceStatistics.ShouldBe(sourceStatistics);
    }

    [Test]
    public void AnArtifactWithoutTheMembers_HasNone_AndWritesNone()
    {
        var artifact = Read("""{"role":"raster.cog","path":"cog/cog.tif","kind":"file","mediaType":"image/tiff"}""");

        artifact.Statistics.ShouldBeNull();
        artifact.SourceStatistics.ShouldBeNull();
        var written = JsonSerializer.Serialize(artifact, NativeJson.Options);
        written.ShouldNotContain("statistics", Case.Insensitive);
    }
}
