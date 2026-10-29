using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// On the real libddb: the objects slice reads libddb's build manifest by role
/// instead of composing the build layout.
/// <list type="bullet">
/// <item>The render source of a built entry is the artifact libddb lists, with the same dataset path the
/// type switch composed (cache keys and native calls unchanged).</item>
/// <item>The build-artifact download keeps today's media type (the pending decision, one table).</item>
/// <item>A transfer copies the build with <c>DDBCopyBuildArtifacts</c>: a deferred build stays deferred
/// in the destination (the hand-written folder copy lost the <c>&lt;hash&gt;.pending</c> marker).</item>
/// </list>
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~BuildManifestTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class BuildManifestTests
{
    private const string Org = "org";
    private static readonly string GeoRasterUrl = TestData.Url("testdata/raster/byte.tif");

    private readonly NativeDdbWrapper _wrapper = new();
    private BuildArtifactResolver _resolver = null!;
    private string _root = null!;
    private string _geoRaster = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() =>
        _geoRaster = TestDownloadCache.GetOrDownload(GeoRasterUrl,
            Path.Combine(Path.GetTempPath(), nameof(BuildManifestTests)));

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "buildmanifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _resolver = TestPorts.Artifacts(_wrapper);
    }

    [TearDown]
    public void TearDown()
    {
        _resolver.Dispose();
        foreach (var folder in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                     .Where(d => File.Exists(Path.Combine(d, ".ddb", "dbase.sqlite"))))
            _wrapper.ReleaseConnections(folder);
        Directory.Delete(_root, true);
    }

    private DDB NewDataset(string folder)
    {
        Directory.CreateDirectory(folder);
        var ddb = new DDB(folder, _wrapper);
        ddb.Init();
        return ddb;
    }

    private DDB DatasetWithGeoRaster(string name = "ds")
    {
        var ddb = NewDataset(Path.Combine(_root, name));
        File.Copy(_geoRaster, Path.Combine(ddb.DatasetFolderPath, "ortho.tif"));
        ddb.AddRaw("ortho.tif");
        return ddb;
    }

    [Test]
    public void RenderSource_OfABuiltGeoRaster_IsItsCog_WithTheDatasetPathOfBefore()
    {
        var ddb = DatasetWithGeoRaster();
        ddb.Build("ortho.tif");
        var entry = ddb.GetEntry("ortho.tif")!;

        // What GetBuildSource composed: SafeCombine(".ddb", "build", hash, "cog", "cog.tif")
        _resolver.GetRenderSource(ddb, entry).ShouldBe($".ddb/build/{entry.Hash}/cog/cog.tif");
        File.Exists(ddb.GetLocalPath(_resolver.GetRenderSource(ddb, entry)!)).ShouldBeTrue();
    }

    [Test]
    public void RenderSource_BeforeTheBuild_IsNotAvailable_AndAFileThatIsNotRenderedIsItself()
    {
        var ddb = DatasetWithGeoRaster();
        File.WriteAllText(Path.Combine(ddb.DatasetFolderPath, "notes.txt"), "text");
        ddb.AddRaw("notes.txt");

        _resolver.GetRenderSource(ddb, ddb.GetEntry("ortho.tif")!).ShouldBeNull();
        _resolver.GetRenderSource(ddb, ddb.GetEntry("notes.txt")!).ShouldBe("notes.txt");
    }

    [Test]
    public void Download_OfABuiltGeoRaster_IsItsCog_WithTheManifestsMediaType()
    {
        var ddb = DatasetWithGeoRaster();
        ddb.Build("ortho.tif");

        var download = _resolver.GetDownload(_resolver.GetBuild(ddb, "ortho.tif")).ShouldNotBeNull();

        File.Exists(download.LocalPath).ShouldBeTrue(download.LocalPath);
        Path.IsPathRooted(download.LocalPath).ShouldBeTrue();
        download.FileName.ShouldBe("cog.tif");
        download.MediaType.ShouldBe("image/tiff; application=geotiff; profile=cloud-optimized");
    }

    [Test]
    public void ResolveFile_IsAFileOfTheBuild_OrNull()
    {
        var ddb = DatasetWithGeoRaster();
        ddb.Build("ortho.tif");
        var build = _resolver.GetBuild(ddb, "ortho.tif").Build!;
        var cog = build.Artifacts.Single(a => a.Role == ArtifactRoles.RasterCog);

        _resolver.ResolveFile(ddb, build.Hash!, cog.Path)!.Path.ShouldBe(build.LocalPathOf(cog));
        _resolver.ResolveFile(ddb, build.Hash!, "cog/missing.tif").ShouldBeNull();
        Should.Throw<DdbInvalidArgumentsException>(() => _resolver.ResolveFile(ddb, build.Hash!, "../../dbase.sqlite"));
    }

    // The URL an entry listing hands to the Hub names, in its path, the version libddb reports for the file behind it,
    // so Registry serves that URL as immutable; a forced rebuild rewrites the COG under the same hash and the listing
    // hands out a new URL, which the browser has never kept
    [Test]
    public void ArtifactLink_NamesTheVersionOfItsFile_AndAForcedRebuildGivesANewOne()
    {
        var ddb = DatasetWithGeoRaster();
        ddb.Build("ortho.tif");
        var urls = TestPorts.Urls(Microsoft.Extensions.Options.Options.Create(
            new HttpHostOptions { ExternalUrlOverride = "https://hub.example" }));
        var hash = ddb.GetEntry("ortho.tif")!.FileHash();
        string CogLink() => urls.ArtifactLinks(Org, "ds", _resolver.GetBuild(ddb, "ortho.tif"))
            .Single(l => l.Role == ArtifactRoles.RasterCog).Url;
        string? CogVersion() => _resolver.ResolveFile(ddb, hash, "cog/cog.tif")!.Version;

        var before = CogVersion().ShouldNotBeNull();
        CogLink().ShouldBe($"https://hub.example/orgs/{Org}/ds/ds/build/{hash}@{before}/cog/cog.tif");

        ddb.Build("ortho.tif", force: true);

        var after = CogVersion().ShouldNotBeNull();
        after.ShouldNotBe(before);
        CogLink().ShouldBe($"https://hub.example/orgs/{Org}/ds/ds/build/{hash}@{after}/cog/cog.tif");
    }

    [Test]
    public async Task Transfer_OfADeferredBuild_KeepsItDeferredInTheDestination()
    {
        var sourceRef = Guid.NewGuid();
        var destinationRef = Guid.NewGuid();
        var source = NewDataset(Path.Combine(_root, Org, sourceRef.ToString()));
        NewDataset(Path.Combine(_root, Org, destinationRef.ToString()));

        // A Shapefile without its .shx and .dbf: libddb defers its build (a pending marker)
        File.WriteAllText(Path.Combine(source.DatasetFolderPath, "roads.shp"), "not a real shapefile");
        source.AddRaw("roads.shp");
        source.Build("roads.shp");
        _resolver.GetBuild(source, "roads.shp").Build!.State.ShouldBe(BuildState.Pending);

        await using var context = TestDb.CreateContext<RegistryContext>();
        var org = Seed.Organization(context, Org);
        var sourceDs = Seed.Dataset(context, org, "src", sourceRef);
        var destinationDs = Seed.Dataset(context, org, "dst", destinationRef);
        await context.SaveChangesAsync();

        await Transfers(context, sourceDs, destinationDs).TransferAsync(Org, "src", "roads.shp", Org, "dst", "roads.shp");

        var destination = new DDB(Path.Combine(_root, Org, destinationRef.ToString()), _wrapper);
        var moved = _resolver.GetBuild(destination, "roads.shp");
        moved.Exists.ShouldBeTrue();
        moved.Build!.State.ShouldBe(BuildState.Pending, "the deferred build stays deferred");
        moved.Build.MissingDependencies.ShouldNotBeEmpty();
    }

    private IEntryTransferService Transfers(RegistryContext context, params Dataset[] datasets)
    {
        var settings = TestSettings.Options(new AppSettings { DatasetsPath = _root, EnableStorageLimiter = false });
        var utils = new Mock<IDatasetResolver>();
        foreach (var dataset in datasets)
            utils.Setup(u => u.GetDatasetAsync(Org, dataset.Slug, It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        return new ObjectServicesBuilder(context)
            .WithSettings(settings)
            .WithDdbManager(new DdbManager(TestSettings.Options<StorageOptions>(settings),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DdbManager>.Instance, _wrapper))
            .WithDatasets(utils.Object)
            .WithAuthManager(auth.Object)
            .WithDdbWrapper(_wrapper)
            .WithBuildArtifactResolver(_resolver)
            .Build().Transfers;
    }
}
