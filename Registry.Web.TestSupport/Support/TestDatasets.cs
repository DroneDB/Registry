using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>The datasets API (a facade over queries, commands and the mover) over test collaborators.</summary>
public static class TestDatasets
{
    public static DatasetsManager Manager(RegistryContext context, WebPorts ports, IDdbManager ddbManager,
        IAuthManager authManager, IDatasetListingInvalidator listings, IFileSystem fileSystem,
        IBackgroundJobsProcessor jobs, IOptions<ThumbnailOptions> thumbnails)
    {
        var access = new DatasetAccessResolver(ports.Datasets, authManager, ddbManager, TestPaths.Policy);
        var commands = new DatasetCommands(context, ports.Datasets, access, ddbManager, listings,
            Removal(context, jobs, listings), NullLogger<DatasetCommands>.Instance);
        return new DatasetsManager(
            new DatasetQueries(access, authManager, context,
                new DatasetListState(TestCaches.Summaries(ddbManager), new DatasetActivity(context)), ddbManager,
                ports.Urls, thumbnails),
            commands,
            new DatasetMover(context, ports.Datasets, commands, ddbManager, fileSystem, listings,
                NullLogger<DatasetMover>.Instance));
    }

    /// <summary>
    /// The removal of a dataset (<see cref="DatasetRemoval"/>) on the test's database: <paramref name="jobs"/> gets the
    /// cleanup job and the cancels of the tasks that hold the dataset.
    /// </summary>
    public static DatasetRemoval Removal(RegistryContext context, IBackgroundJobsProcessor jobs,
        IDatasetListingInvalidator? listings = null) =>
        new(context, listings ?? Mock.Of<IDatasetListingInvalidator>(), jobs, new DatasetActivity(context),
            TestJobs.Canceller(context, jobs), NullLogger<DatasetRemoval>.Instance);

    /// <summary>
    /// The datasets API for a test that renames and moves datasets: the caller is an administrator (every access is
    /// granted), there are no dataset folders and no native library, and what the database holds is what is checked.
    /// <paramref name="jobs"/> is the background job client (a mock that records what is queued, by default).
    /// </summary>
    public static DatasetsManager AdminManager(RegistryContext context, IBackgroundJobsProcessor? jobs = null)
    {
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { UserName = "admin", Id = "admin-id" });
        auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(true);
        auth.Setup(a => a.RequestAccess(It.IsAny<Registry.Web.Data.Models.Dataset>(), It.IsAny<AccessType>()))
            .ReturnsAsync(true);

        var ddbManager = new Mock<IDdbManager>().WithFolders("test-datasets");
        var ports = TestPorts.All(context, Microsoft.Extensions.Options.Options.Create(new AccountOptions()),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions()), new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            TestCaches.Summaries(ddbManager.Object));
        return Manager(context, ports, ddbManager.Object, auth.Object, Mock.Of<IDatasetListingInvalidator>(),
            new Registry.Adapters.FileSystem(), jobs ?? Mock.Of<IBackgroundJobsProcessor>(),
            Microsoft.Extensions.Options.Options.Create(new ThumbnailOptions()));
    }
}
