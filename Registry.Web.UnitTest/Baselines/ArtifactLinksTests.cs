#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The entry DTO carries <c>artifacts: [{role, url, mediaType}]</c> from libddb's build
/// manifest, so a client needs no build path table and no HEAD probe. The URL is the generic build-file route (a
/// directory artifact's descriptor), the media type the served one (Registry's single table, else the
/// manifest's), and a role Registry does not know passes through unchanged.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ArtifactLinksTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class ArtifactLinksTests
{
    private const string Folder = "/datasets/org/ds";
    private const string Hash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";

    private static readonly UrlBuilder Urls = TestPorts.Urls(Microsoft.Extensions.Options.Options.Create(
        new HttpHostOptions { ExternalUrlOverride = "https://hub.example" }));

    [Test]
    public void File_Directory_AndUnknownRoles_BecomeLinks()
    {
        var build = TestBuilds.Build(Folder, "layer.geojson", EntryType.Vector, Hash, BuildState.Complete,
            TestBuilds.File("vector.gpkg", "vec/source.gpkg", "application/geopackage+sqlite3"),
            TestBuilds.Directory("vector.mvt", "mvt", "metadata.json"),
            TestBuilds.File("vector.future", "future/data.bin", "application/x-future"));

        var links = Urls.ArtifactLinks("org", "ds", build);

        links.Select(l => (l.Role, l.Url, l.MediaType)).ShouldBe(
        [
            ("vector.gpkg", $"https://hub.example/orgs/org/ds/ds/build/{Hash}/vec/source.gpkg", "application/geopackage+sqlite3"),
            ("vector.mvt", $"https://hub.example/orgs/org/ds/ds/build/{Hash}/mvt/metadata.json", "application/json"),
            ("vector.future", $"https://hub.example/orgs/org/ds/ds/build/{Hash}/future/data.bin", "application/x-future")
        ]);
    }

    // libddb's version of each artifact goes in the path of its URL (build/{hash}@{version}/...), where the relative
    // references of a directory (its tiles) keep it: Registry serves a URL at the current version as immutable
    [Test]
    public void TheVersionOfEachArtifact_IsInThePathOfItsLink()
    {
        var build = TestBuilds.Build(Folder, "splat.ply", EntryType.GaussianSplat, Hash, BuildState.Complete,
            TestBuilds.File("gsplat.lod", "gsplat/model.rad", version: "3f2a"),
            TestBuilds.File("gsplat.bounds", "gsplat/bounds.json", "application/json", version: "91c"),
            TestBuilds.Directory("model.3dtiles", "3dtiles", "tileset.json", version: "77c1"));

        var links = Urls.ArtifactLinks("org", "ds", build);

        links.Select(l => (l.Role, l.Url)).ShouldBe(
        [
            ("gsplat.lod", $"https://hub.example/orgs/org/ds/ds/build/{Hash}@3f2a/gsplat/model.rad"),
            ("gsplat.bounds", $"https://hub.example/orgs/org/ds/ds/build/{Hash}@91c/gsplat/bounds.json"),
            ("model.3dtiles", $"https://hub.example/orgs/org/ds/ds/build/{Hash}@77c1/3dtiles/tileset.json")
        ]);
    }

    [Test]
    public void ASidecar_HasNoLinks_ItsBuildIsItsMainFiles()
    {
        var sidecar = TestBuilds.Build(Folder, "layer.prj", EntryType.Generic, Hash, BuildState.Complete,
            TestBuilds.File("vector.gpkg", "vec/source.gpkg"));
        sidecar.Build!.Source = "layer.shp";

        Urls.ArtifactLinks("org", "ds", sidecar).ShouldBeEmpty();
    }

    [Test]
    public async Task Listing_CarriesTheArtifactsOfBuiltEntries_AndNothingForTheOthers()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        ddb.SetupBuilds(
            TestBuilds.Build(Folder, "ortho.tif", EntryType.GeoRaster, Hash, BuildState.Complete,
                TestBuilds.File("raster.cog", "cog/cog.tif", "image/tiff; application=geotiff; profile=cloud-optimized")),
            TestBuilds.Build(Folder, "cloud.las", EntryType.PointCloud, "h2", BuildState.None));
        var entries = new List<EntryDto>
        {
            new() { Path = "ortho.tif", Type = EntryType.GeoRaster },
            new() { Path = "cloud.las", Type = EntryType.PointCloud },
            new() { Path = "img.jpg", Type = EntryType.Image }
        };
        var service = new BuildStatusService(Mock.Of<IJobIndexQuery>(q =>
                q.GetActiveJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyCollection<string>>(), default) == Task.FromResult<IReadOnlyList<ActiveJob>>(System.Array.Empty<ActiveJob>()) &&
                q.GetLatestStatesByPathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyCollection<string>>(), default) == Task.FromResult(new Dictionary<string, string>())),
            TestPorts.Artifacts(), Urls,
            NullLogger<BuildStatusService>.Instance);

        await service.AnnotateAsync("org", "ds", ddb.Object, entries);

        entries[0].Artifacts!.Single().ShouldSatisfyAllConditions(
            a => a.Role.ShouldBe("raster.cog"),
            a => a.Url.ShouldBe($"https://hub.example/orgs/org/ds/ds/build/{Hash}/cog/cog.tif"),
            a => a.MediaType.ShouldBe("image/tiff; application=geotiff; profile=cloud-optimized"));
        entries[1].Artifacts.ShouldBeNull("nothing built yet: the field is absent");
        entries[2].Artifacts.ShouldBeNull("not a buildable type");
    }
}
