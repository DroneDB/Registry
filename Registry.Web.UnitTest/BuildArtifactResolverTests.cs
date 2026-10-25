#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The role choices of <see cref="BuildArtifactResolver"/> over libddb's build manifest: which
/// artifact a renderer reads, which one is downloaded and with which media type, which builds a transfer
/// copies. The entry types come from the libddb contract fixture (<c>entry-types.json</c>).
/// </summary>
[TestFixture]
public class BuildArtifactResolverTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "buildartifactresolver-resolver", "ds");
    private const string Hash = "abc123";

    private Mock<IDDB> _ddb = null!;
    private BuildArtifactResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        _resolver = TestPorts.Artifacts();
    }

    private static Entry Entry(string path, EntryType type) => new() { Path = path, Type = type, Hash = Hash };

    private static EntryBuildArtifacts Built(string path, EntryType type, params BuildArtifact[] artifacts) =>
        TestBuilds.Build(Folder, path, type, Hash, BuildState.Complete, artifacts);

    private static readonly BuildArtifact Cog = TestBuilds.File(ArtifactRoles.RasterCog, "cog/cog.tif",
        "image/tiff; application=geotiff; profile=cloud-optimized", "cog.tif");

    [Test]
    public void RenderSource_IsTheArtifactARendererReads_AsTheDatasetPathOfBefore()
    {
        _ddb.SetupBuilds(Built("ortho.tif", EntryType.GeoRaster, Cog));

        _resolver.GetRenderSource(_ddb.Object, Entry("ortho.tif", EntryType.GeoRaster))
            .ShouldBe($".ddb/build/{Hash}/cog/cog.tif");
    }

    [Test]
    public void RenderSource_PrefersTheRenderedRole_OverTheOtherArtifactsOfTheBuild()
    {
        // The vector build lists its tiles and drawing metadata too: the renderer reads the GeoPackage
        _ddb.SetupBuilds(Built("roads.geojson", EntryType.Vector,
            TestBuilds.Directory(ArtifactRoles.VectorMvt, "mvt", "metadata.json"),
            TestBuilds.File(ArtifactRoles.VectorGpkg, "vec/source.gpkg")));

        _resolver.GetRenderSource(_ddb.Object, Entry("roads.geojson", EntryType.Vector))
            .ShouldBe($".ddb/build/{Hash}/vec/source.gpkg");
    }

    [Test]
    public void RenderSource_OfALegacyDirectoryArtifact_IsItsDescriptor()
    {
        _ddb.SetupBuilds(Built("cloud.laz", EntryType.PointCloud,
            TestBuilds.Directory(ArtifactRoles.PointCloudEpt, "ept", "ept.json", legacy: true)));

        _resolver.GetRenderSource(_ddb.Object, Entry("cloud.laz", EntryType.PointCloud))
            .ShouldBe($".ddb/build/{Hash}/ept/ept.json");
    }

    [Test]
    public void RenderSource_WithoutTheRenderedArtifactYet_IsNotAvailable()
    {
        // Only the tiles exist (a partial build): the renderer's GeoPackage is not there
        _ddb.SetupBuilds(TestBuilds.Build(Folder, "roads.geojson", EntryType.Vector, Hash, BuildState.Building,
            TestBuilds.Directory(ArtifactRoles.VectorMvt, "mvt", "metadata.json")));

        _resolver.GetRenderSource(_ddb.Object, Entry("roads.geojson", EntryType.Vector)).ShouldBeNull();
    }

    [Test]
    public void RenderSource_OfASidecar_IsTheFileItself()
    {
        // A sidecar is buildable as part of its main file: the build and its artifacts are the main file's
        var sidecar = Built("roads.shp", EntryType.Vector, TestBuilds.File(ArtifactRoles.VectorGpkg, "vec/source.gpkg"));
        sidecar.Path = "roads.dbf";
        _ddb.SetupBuilds(sidecar);

        _resolver.GetRenderSource(_ddb.Object, Entry("roads.dbf", EntryType.Vector)).ShouldBe("roads.dbf");
    }

    [TestCase(EntryType.Tiles3D)]
    [TestCase(EntryType.GeoImage)]
    [TestCase(EntryType.Generic)]
    public void RenderSource_OfATypeNeverRenderedFromABuild_IsTheFile_WithoutANativeCall(EntryType type)
    {
        _resolver.GetRenderSource(_ddb.Object, Entry("file", type)).ShouldBe("file");

        _ddb.Verify(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    private static IEnumerable<TestCaseData> ServedTypes()
    {
        yield return new TestCaseData(Cog, "image/tiff; application=geotiff; profile=cloud-optimized").SetName("{m}(cog)");
        yield return new TestCaseData(TestBuilds.File(ArtifactRoles.PointCloudCopc, "copc/cloud.copc.laz",
            "application/vnd.laszip+copc", "cloud.copc.laz"), "application/vnd.laszip+copc").SetName("{m}(copc)");
        yield return new TestCaseData(TestBuilds.File(ArtifactRoles.VectorGpkg, "vec/source.gpkg",
            "application/geopackage+sqlite3", "source.gpkg"), "application/geopackage+sqlite3").SetName("{m}(gpkg)");
        // Every role is served as libddb describes it (a new format needs no change)
        yield return new TestCaseData(TestBuilds.File("future.role", "future/data.bin", "application/x-future",
            "data.bin"), "application/x-future").SetName("{m}(unknown role)");
    }

    [TestCaseSource(nameof(ServedTypes))]
    public void Download_IsTheArtifactWithADownloadName_ServedWithTheManifestsMediaType(BuildArtifact artifact,
        string served)
    {
        var download = _resolver.GetDownload(Built("entry", EntryType.GeoRaster,
            TestBuilds.File(ArtifactRoles.GsplatBounds, "gsplat/bounds.json", "application/json"), artifact))!;

        download.ShouldNotBeNull();
        download.FileName.ShouldBe(artifact.DownloadName);
        download.MediaType.ShouldBe(served);
        download.LocalPath.ShouldBe(Path.GetFullPath(Path.Combine(Folder, ".ddb", "build", Hash,
            artifact.Path.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Test]
    public void Download_WithoutADownloadableArtifactOrOfAnotherEntrysBuild_IsNone()
    {
        _resolver.GetDownload(Built("model.obj", EntryType.Model,
            TestBuilds.File(ArtifactRoles.ModelNexus, "nxs/model.nxz"))).ShouldBeNull();
        _resolver.GetDownload(TestBuilds.NotBuildable("photo.jpg", EntryType.Image)).ShouldBeNull();

        var sidecar = Built("roads.shp", EntryType.Vector, TestBuilds.File(ArtifactRoles.VectorGpkg,
            "vec/source.gpkg", "application/geopackage+sqlite3", "source.gpkg"));
        sidecar.Path = "roads.prj";
        _resolver.GetDownload(sidecar).ShouldBeNull();
    }

    [Test]
    public void CopyBuilds_CopiesEachBuildWithSomethingToKeep_Once()
    {
        var destination = new Mock<IDDB>();
        destination.SetupGet(d => d.DatasetFolderPath).Returns("/datasets/destination");
        var sameContent = Built("b/ortho.tif", EntryType.GeoRaster, Cog);
        var pending = TestBuilds.Pending(Folder, "roads.shp", EntryType.Vector, "pending-hash", "roads.shx");
        var notBuiltYet = TestBuilds.Build(Folder, "late.tif", EntryType.GeoRaster, "late-hash", BuildState.None);
        _ddb.SetupBuilds(Built("a/ortho.tif", EntryType.GeoRaster, Cog), sameContent, pending, notBuiltYet,
            TestBuilds.NotBuildable("notes.txt"));

        var copied = _resolver.CopyBuilds(_ddb.Object,
            ["a/ortho.tif", "b/ortho.tif", "roads.shp", "late.tif", "notes.txt"], destination.Object);

        copied.ShouldBe(2);
        _ddb.Verify(d => d.CopyBuildArtifactsTo("/datasets/destination", Hash), Times.Once);
        _ddb.Verify(d => d.CopyBuildArtifactsTo("/datasets/destination", "pending-hash"), Times.Once);
        _ddb.Verify(d => d.CopyBuildArtifactsTo(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        _ddb.Verify(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Once);
    }

    [Test]
    public void GetBuilds_AnswerOfAnotherLength_IsAnError()
    {
        _ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns([]);

        Should.Throw<DdbException>(() => _resolver.GetBuilds(_ddb.Object, ["a", "b"]));
    }
}
