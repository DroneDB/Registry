using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
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

    [TearDown]
    public void TearDown() => _resolver.Dispose();

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

    [TestCase(ArtifactRoles.ModelBm3d, "bm3d/model.bm3d", false)]
    [TestCase(ArtifactRoles.ModelNexus, "nxs/model.nxz", true)]
    public void RenderSource_OfAModel_IsTheMeshItsBuildListsWhicheverLayoutBuiltIt(string role, string path,
        bool legacy)
    {
        // libddb lists the Nexus mesh of an older build only while there is no bm3d one, and never both
        _ddb.SetupBuilds(Built("model.obj", EntryType.Model,
            TestBuilds.File(role, path, legacy: legacy),
            TestBuilds.Directory(ArtifactRoles.Model3DTiles, "3dtiles", "tileset.json")));

        _resolver.GetRenderSource(_ddb.Object, Entry("model.obj", EntryType.Model))
            .ShouldBe($".ddb/build/{Hash}/{path}");
    }

    [Test]
    public void RenderSource_OfAGaussianSplat_IsItsLodModel_NotItsBoundsOrItsSpzFile()
    {
        // The splat thumbnail is drawn from the streamed model; the bounds sidecar and the SPZ file are listed too
        _ddb.SetupBuilds(Built("scene.ply", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatBounds, "gsplat/bounds.json", "application/json"),
            TestBuilds.File(ArtifactRoles.GsplatLod, "gsplat/model.rad"),
            TestBuilds.File(ArtifactRoles.GsplatSpz, "gsplat/model.spz")));

        _resolver.GetRenderSource(_ddb.Object, Entry("scene.ply", EntryType.GaussianSplat))
            .ShouldBe($".ddb/build/{Hash}/gsplat/model.rad");
    }

    [Test]
    public void RenderSource_OfAGaussianSplat_IsItsLodModel_WhenItIsTheOnlyModelFile()
    {
        _ddb.SetupBuilds(Built("scene.ply", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatLod, "gsplat/model.rad")));

        _resolver.GetRenderSource(_ddb.Object, Entry("scene.ply", EntryType.GaussianSplat))
            .ShouldBe($".ddb/build/{Hash}/gsplat/model.rad");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RenderSource_OfAGaussianSplatBuiltBeforeTheLodModel_IsItsSpzFile(bool flaggedLegacy)
    {
        // The splat has only the SPZ file an older build wrote: the viewer opens it, so the thumbnail is drawn from
        // it too, whether or not libddb flags the file as the legacy stand-in of the LOD model
        _ddb.SetupBuilds(Built("scene.spz", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatSpz, "gsplat/model.spz", legacy: flaggedLegacy,
                fallbackFor: flaggedLegacy ? ArtifactRoles.GsplatLod : null)));

        _resolver.GetRenderSource(_ddb.Object, Entry("scene.spz", EntryType.GaussianSplat))
            .ShouldBe($".ddb/build/{Hash}/gsplat/model.spz");
    }

    [Test]
    public void RenderSource_OfAGaussianSplatWithBothModelFiles_IsTheLodModel_WhicheverIsListedFirst()
    {
        // A libddb that does not flag the SPZ file as legacy lists it beside the LOD model of a rebuilt splat
        _ddb.SetupBuilds(Built("scene.ply", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatSpz, "gsplat/model.spz"),
            TestBuilds.File(ArtifactRoles.GsplatLod, "gsplat/model.rad")));

        _resolver.GetRenderSource(_ddb.Object, Entry("scene.ply", EntryType.GaussianSplat))
            .ShouldBe($".ddb/build/{Hash}/gsplat/model.rad");
    }

    [Test]
    public void ListsLegacyArtifact_OfASplatWithOnlyItsSpzFile_FollowsTheFlagOfTheManifest()
    {
        var legacy = Built("scene.spz", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatSpz, "gsplat/model.spz", legacy: true,
                fallbackFor: ArtifactRoles.GsplatLod));
        var current = Built("scene.spz", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatLod, "gsplat/model.rad"),
            TestBuilds.File(ArtifactRoles.GsplatSpz, "gsplat/model.spz"));

        legacy.Build!.ListsLegacyArtifact().ShouldBeTrue();
        current.Build!.ListsLegacyArtifact().ShouldBeFalse();
    }

    [Test]
    public void RenderSource_OfAGaussianSplatWithOnlyItsBounds_IsNotAvailable()
    {
        _ddb.SetupBuilds(Built("scene.ply", EntryType.GaussianSplat,
            TestBuilds.File(ArtifactRoles.GsplatBounds, "gsplat/bounds.json", "application/json")));

        _resolver.GetRenderSource(_ddb.Object, Entry("scene.ply", EntryType.GaussianSplat)).ShouldBeNull();
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
        var downloadName = artifact.DownloadName!;
        download.FileName.ShouldBe("entry" + downloadName[downloadName.IndexOf('.', StringComparison.Ordinal)..]);
        download.MediaType.ShouldBe(served);
        download.IsArchive.ShouldBeFalse();
        download.LocalPath.ShouldBe(Path.GetFullPath(Path.Combine(Folder, ".ddb", "build", Hash,
            artifact.Path.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static readonly BuildArtifact SplatModel = TestBuilds.File(ArtifactRoles.GsplatLod, "gsplat/model.rad",
        "application/octet-stream", "model.rad");

    private static readonly BuildArtifact SplatExport = TestBuilds.File(ArtifactRoles.GsplatExport, "gsplat/export.spz",
        "application/octet-stream", "export.spz");

    [Test]
    public void Download_ByRole_IsThatArtifact_NamedAfterTheEntryWithTheExtensionOfItsDownloadName()
    {
        var build = Built("flights/scene.ply", EntryType.GaussianSplat, SplatModel, SplatExport);

        var download = _resolver.GetDownload(build, ArtifactRoles.GsplatExport)!;

        download.FileName.ShouldBe("scene.spz", "the manifest's export.spz is the same for every entry");
        download.MediaType.ShouldBe("application/octet-stream");
        download.LocalPath.ShouldBe(Path.GetFullPath(Path.Combine(Folder, ".ddb", "build", Hash, "gsplat", "export.spz")));
        _resolver.GetDownload(build, ArtifactRoles.GsplatLod)!.FileName.ShouldBe("scene.rad");
    }

    [Test]
    public void Download_WithoutARole_IsTheFirstArtifactWithADownloadName_NamedAfterTheEntry()
    {
        var build = Built("scene.ply", EntryType.GaussianSplat, SplatModel, SplatExport);

        var download = _resolver.GetDownload(build)!;

        download.FileName.ShouldBe("scene.rad", "the manifest's model.rad is the same for every entry");
        download.LocalPath.ShouldEndWith(Path.Combine("gsplat", "model.rad"));
    }

    [TestCase("ortho.tif", "cog.tif", ExpectedResult = "ortho.tif")]
    [TestCase("ORTHO.TIF", "cog.tif", ExpectedResult = "ORTHO.TIF")]
    [TestCase("flights/dsm.jp2", "cog.tif", ExpectedResult = "dsm.tif")]
    [TestCase("scan.laz", "cloud.copc.laz", ExpectedResult = "scan.copc.laz")]
    [TestCase("scan.copc.laz", "cloud.copc.laz", ExpectedResult = "scan.copc.laz")]
    [TestCase("roads.shp", "source.gpkg", ExpectedResult = "roads.gpkg")]
    [TestCase("site/building.obj", "model.3dtiles.zip", ExpectedResult = "building.3dtiles.zip")]
    [TestCase("noextension", "cog.tif", ExpectedResult = "noextension.tif")]
    [TestCase("scene.ply", "export", ExpectedResult = "scene")]
    public string DownloadFileName_IsTheEntrysName_WithTheExtensionOfTheDownloadNameFromItsFirstDot(string entry,
        string downloadName) => BuildArtifactResolver.DownloadFileName(entry, downloadName);

    [Test]
    public void Download_OfADirectoryArtifact_IsAZipArchiveOfTheDirectory()
    {
        var build = Built("site/building.obj", EntryType.Model,
            TestBuilds.File(ArtifactRoles.ModelBm3d, "bm3d/model.bm3d", "application/vnd.dronedb.bm3d"),
            TestBuilds.Directory(ArtifactRoles.Model3DTiles, "3dtiles", "tileset.json", downloadName: "model.3dtiles.zip"));

        var download = _resolver.GetDownload(build)!;

        download.IsArchive.ShouldBeTrue();
        download.FileName.ShouldBe("building.3dtiles.zip");
        download.MediaType.ShouldBe("application/zip", "the manifest's media type is the one of the tileset");
        download.LocalPath.ShouldBe(Path.GetFullPath(Path.Combine(Folder, ".ddb", "build", Hash, "3dtiles")));
    }

    [Test]
    public void Download_ByRole_OfAnArtifactThatDoesNotExistOrHasNoDownloadName_IsNone()
    {
        // The export is made on demand: until then the build lists the model alone
        _resolver.GetDownload(Built("scene.ply", EntryType.GaussianSplat, SplatModel), ArtifactRoles.GsplatExport)
            .ShouldBeNull();
        _resolver.GetDownload(Built("scene.ply", EntryType.GaussianSplat, SplatModel,
            TestBuilds.File(ArtifactRoles.GsplatBounds, "gsplat/bounds.json", "application/json")),
            ArtifactRoles.GsplatBounds).ShouldBeNull("only an artifact libddb names a download is offered");
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
