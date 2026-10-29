using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Push;
using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>The push API (a facade over the session, the uploads and the commit) over test collaborators.</summary>
public static class TestPush
{
    public static PushManager Manager(IDatasetResolver datasets, IDdbManager ddbManager, IDatasetsManager datasetsManager,
        IAuthManager authManager, IDatasetAccessResolver access, IDdbWrapper ddbWrapper, BuildScheduler builds,
        IOptions<ImportSettings> importSettings, IFileSystem fileSystem, PushSessionStore sessionStore,
        UploadQuotaGuard quota, DatasetPathPolicy paths, ScratchFileProvider scratch) =>
        new(new PushSessions(datasets, datasetsManager, access, ddbManager, ddbWrapper, sessionStore,
                NullLogger<PushSessions>.Instance),
            new PushUploads(access, authManager, quota, scratch, fileSystem, sessionStore, paths),
            new PushCommit(access, sessionStore, ddbWrapper, fileSystem, importSettings, builds));
}
