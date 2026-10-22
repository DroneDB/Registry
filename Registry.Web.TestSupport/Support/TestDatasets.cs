#nullable enable
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>The datasets API (RQ-12: a facade over queries, commands and the mover) over test collaborators.</summary>
public static class TestDatasets
{
    public static DatasetsManager Manager(RegistryContext context, WebPorts ports, IDdbManager ddbManager,
        IAuthManager authManager, IDatasetListingInvalidator listings, IFileSystem fileSystem,
        IBackgroundJobsProcessor jobs, IOptions<ThumbnailOptions> thumbnails)
    {
        var access = new DatasetAccessResolver(ports.Datasets, authManager, ddbManager, TestPaths.Policy);
        var commands = new DatasetCommands(context, ports.Datasets, access, ddbManager, listings, jobs,
            NullLogger<DatasetCommands>.Instance);
        return new DatasetsManager(
            new DatasetQueries(access, authManager, context, TestCaches.Summaries(ddbManager), ddbManager, ports.Urls,
                thumbnails),
            commands,
            new DatasetMover(context, ports.Datasets, commands, ddbManager, fileSystem, listings,
                NullLogger<DatasetMover>.Instance));
    }
}
