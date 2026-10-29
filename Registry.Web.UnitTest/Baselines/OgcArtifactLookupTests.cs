using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The OGC services and the MVT tiles read their artifacts by role
/// (<see cref="BuildArtifactResolver.FindArtifactFile"/>) instead of composing libddb's build layout
/// (<c>IOgcBuildArtifactPaths</c>, deleted). A tile request pays the manifest once per build: the location of a
/// role's artifact is kept while it exists.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~OgcArtifactLookupTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class OgcArtifactLookupTests : IDisposable
{
    private const string Hash = "ab12cd34";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ogcartifactlookup-ogc-" + Guid.NewGuid().ToString("N"));
    private Mock<IDDB> _ddb = null!;
    private BuildArtifactResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        var mvt = Path.Combine(_folder, ".ddb", "build", Hash, "mvt");
        Directory.CreateDirectory(Path.Combine(mvt, "7", "12"));
        File.WriteAllText(Path.Combine(mvt, "metadata.json"), "{}");
        File.WriteAllText(Path.Combine(mvt, "7", "12", "34.pbf"), "tile");
        Directory.CreateDirectory(Path.Combine(_folder, ".ddb", "build", Hash, "vec"));
        File.WriteAllText(Path.Combine(_folder, ".ddb", "build", Hash, "vec", "source.gpkg"), "gpkg");

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(_folder);
        _ddb.SetupBuilds(TestBuilds.Build(_folder, "roads.geojson", EntryType.Vector, Hash, BuildState.Complete,
            TestBuilds.Directory(ArtifactRoles.VectorMvt, "mvt", "metadata.json"),
            TestBuilds.File(ArtifactRoles.VectorGpkg, "vec/source.gpkg")));
        _ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new EntrySummary { Path = "roads.geojson", Hash = Hash, Type = EntryType.Vector }]);
        _resolver = TestPorts.Artifacts();
    }

    [Test]
    public void FileArtifact_DirectoryDescriptor_AndAFileInside()
    {
        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorGpkg, entryPath: "roads.geojson")
            .ShouldBe(Path.Combine(_folder, ".ddb", "build", Hash, "vec", "source.gpkg"));
        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorMvt, entryPath: "roads.geojson")
            .ShouldBe(Path.Combine(_folder, ".ddb", "build", Hash, "mvt", "metadata.json"));
        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorMvt, MvtTiles.Tile(7, 12, 34), "roads.geojson")
            .ShouldBe(Path.Combine(_folder, ".ddb", "build", Hash, "mvt", "7", "12", "34.pbf"));
        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorMvt, MvtTiles.Tile(7, 12, 35), "roads.geojson")
            .ShouldBeNull("a tile outside the pyramid");
    }

    [Test]
    public void ByHashOnly_TheEntryIsFoundAmongTheTypesThatProduceTheRole()
    {
        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorMvt, MvtTiles.Tile(7, 12, 34))
            .ShouldNotBeNull();
        _ddb.Verify(d => d.FindSummaries(It.Is<EntryQuery>(q => q.Types != null && q.Types.Contains(EntryType.Vector))),
            Times.Once);
        _resolver.FindArtifactFile(_ddb.Object, "ffff0000", ArtifactRoles.VectorMvt).ShouldBeNull("no build has that hash");
    }

    [Test]
    public void ManyTiles_OneManifestCall()
    {
        for (var i = 0; i < 50; i++)
            _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorMvt, MvtTiles.Tile(7, 12, 34));

        _ddb.Verify(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Once);
    }

    [Test]
    public void AnArtifactRemoved_IsNotServedFromTheCache()
    {
        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorGpkg, entryPath: "roads.geojson").ShouldNotBeNull();
        Directory.Delete(Path.Combine(_folder, ".ddb", "build", Hash, "vec"), true);
        _ddb.SetupBuilds(TestBuilds.Build(_folder, "roads.geojson", EntryType.Vector, Hash, BuildState.None));

        _resolver.FindArtifactFile(_ddb.Object, Hash, ArtifactRoles.VectorGpkg, entryPath: "roads.geojson").ShouldBeNull();
    }

    public void Dispose()
    {
        _resolver?.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }
}
