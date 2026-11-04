using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// The OGC layer list of a dataset is cached; when a build in it ends the cache is renewed, so a raster whose COG was
/// just built is offered at once instead of when the cached list expires.
/// </summary>
[TestFixture]
public class BuildOgcCacheFilterTests
{
    private const string JobId = "build-job";
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Cog = "/data/ortho-cog.tif";

    private static readonly Entry Ortho = new()
    {
        Path = "ortho.tif",
        Hash = "hash-ortho",
        Type = EntryType.GeoRaster,
        PolygonGeometry = JsonDocument.Parse(
            "{\"type\":\"Polygon\",\"coordinates\":[[[10,40],[11,40],[11,41],[10,41],[10,40]]]}").RootElement
    };

    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private OgcLayerCatalog _catalog = null!;
    private BuildOgcCacheFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var datasets = new Mock<IDatasetResolver>();
        datasets.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([Ortho]);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(ddb.Object);
        // No COG yet: the build is running
        _artifacts = new Mock<IBuildArtifactResolver>();

        // The catalog and the filter share the cache, as the web host and the processing node do
        IDistributedCache store = TestCaches.MemoryStore();
        var keys = TestCaches.OgcKeys(store);
        _catalog = new OgcLayerCatalog(ddbManager.Object, datasets.Object, TestCaches.Records(store), _artifacts.Object,
            Mock.Of<IDdbWrapper>(), keys, NullLogger<OgcLayerCatalog>.Instance);

        _filter = new BuildOgcCacheFilter(keys, NullLogger<BuildOgcCacheFilter>.Instance);
    }

    private async Task<bool> OrthoIsBuilt() =>
        (await _catalog.GetLayersAsync(Org, Ds)).Single(l => l.EntryPath == "ortho.tif").HasBuiltArtifact;

    // The build writes the COG; the list cached while it ran still says the raster has none
    private async Task BuildTheCogAfterTheListWasCached()
    {
        (await OrthoIsBuilt()).ShouldBeFalse();
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), Ortho.Hash!, ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns(Cog);
        (await OrthoIsBuilt()).ShouldBeFalse("the cached list");
    }

    private void Apply(Job job, IState state)
    {
        var connection = new Mock<JobStorageConnection>();
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.OrgSlug)).Returns(Org);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.DsSlug)).Returns(Ds);
        var context = new ApplyStateContext(new InMemoryStorage(), connection.Object, Mock.Of<IWriteOnlyTransaction>(),
            new BackgroundJob(JobId, job, DateTime.UtcNow), state, ProcessingState.StateName);

        _filter.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
    }

    private static Job EntryBuild() => Job.FromExpression<DdbJobs>(j => j.Build(Org, Guid.Empty, "ortho.tif", false, null));

    [Test]
    public async Task ASucceededBuild_OffersTheRasterAtOnce()
    {
        await BuildTheCogAfterTheListWasCached();

        Apply(EntryBuild(), new SucceededState(null, 0, 0));

        (await OrthoIsBuilt()).ShouldBeTrue();
    }

    [Test]
    public async Task AFailedBuild_RenewsTheListToo()
    {
        // A build of the whole dataset can fail after it built some of its rasters
        await BuildTheCogAfterTheListWasCached();

        Apply(Job.FromExpression<DdbJobs>(j => j.Build(Org, Guid.Empty, null, false, null)),
            new FailedState(new DdbException("cannot read")));

        (await OrthoIsBuilt()).ShouldBeTrue();
    }

    [Test]
    public async Task ABackgroundRebuild_RenewsTheList()
    {
        await BuildTheCogAfterTheListWasCached();

        Apply(Job.FromExpression<DdbJobs>(j => j.RebuildInBackground(Org, Guid.Empty, "ortho.tif", null)),
            new SucceededState(null, 0, 0));

        (await OrthoIsBuilt()).ShouldBeTrue();
    }

    [Test]
    public async Task AJobThatDoesNotBuild_OrABuildThatHasNotEnded_KeepsTheList()
    {
        await BuildTheCogAfterTheListWasCached();

        Apply(Job.FromExpression<DdbJobs>(j => j.Cleanup(Org, Guid.Empty, null)), new SucceededState(null, 0, 0));
        Apply(EntryBuild(), new EnqueuedState());

        (await OrthoIsBuilt()).ShouldBeFalse();
    }

    [Test]
    public void TheFilter_IsInstalledOnEveryHost() =>
        RegistryJobsServiceCollectionExtensions.JobFilterTypes.ShouldContain(typeof(BuildOgcCacheFilter));
}
