using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// The answer of <c>DDBResolveBuildFile</c> (<see cref="NativeDdbWrapper.ParseResolvedBuildFile"/>, pure: no libddb):
/// a plain path from the libddb builds that give nothing else, a JSON object with the manifest's media type from
/// those that do. Registry must read both, because it runs on the pinned build until the library is re-pinned.
/// </summary>
[TestFixture]
public class ResolvedBuildFileParsingTests
{
    [TestCase("/data/ds/.ddb/build/abc/gsplat/model.rad")]
    [TestCase("C:/data/ds/.ddb/build/abc/cog/cog.tif")]
    public void APlainPath_IsTheFile_WithoutAMediaType(string output)
    {
        NativeDdbWrapper.ParseResolvedBuildFile(output).ShouldBe(new ResolvedBuildFile(output));
    }

    [Test]
    public void AnObject_GivesThePathAndTheMediaType()
    {
        var file = NativeDdbWrapper.ParseResolvedBuildFile(
            """{"path":"/data/ds/.ddb/build/abc/gsplat/model.rad","mediaType":"application/octet-stream"}""");

        file.ShouldBe(new ResolvedBuildFile("/data/ds/.ddb/build/abc/gsplat/model.rad", "application/octet-stream"));
    }

    [TestCase("""{"path":"/b/x.json"}""")]
    [TestCase("""{"path":"/b/x.json","mediaType":null}""")]
    [TestCase("""{"path":"/b/x.json","mediaType":""}""")]
    public void AnObjectWithoutAMediaType_LeavesItToTheCaller(string output)
    {
        NativeDdbWrapper.ParseResolvedBuildFile(output).ShouldBe(new ResolvedBuildFile("/b/x.json"));
    }

    [Test]
    public void AnObject_GivesTheVersionOfTheArtifact()
    {
        NativeDdbWrapper.ParseResolvedBuildFile("""{"path":"/b/3dtiles/tiles/0.b3dm","version":"77c1"}""")
            .ShouldBe(new ResolvedBuildFile("/b/3dtiles/tiles/0.b3dm", null, "77c1"));
    }

    [TestCase("""{"path":"/b/x.json","version":null}""")]
    [TestCase("""{"path":"/b/x.json","version":""}""")]
    [TestCase("""{"path":"/b/x.json","version":7}""")]
    public void AnObjectWithoutAVersion_HasNone(string output)
    {
        NativeDdbWrapper.ParseResolvedBuildFile(output).Version.ShouldBeNull();
    }

    [TestCase("""{"mediaType":"image/tiff"}""")]
    [TestCase("""{"path":"","mediaType":"image/tiff"}""")]
    [TestCase("""{"path":3}""")]
    public void AnObjectWithoutAPath_IsAnError(string output)
    {
        Should.Throw<DdbException>(() => NativeDdbWrapper.ParseResolvedBuildFile(output))
            .Message.ShouldContain("returned no path");
    }
}
