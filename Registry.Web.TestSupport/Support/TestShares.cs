#nullable enable
using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;
using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>The share API (RQ-12: a facade over the batch, rollback, target and upload services) over test collaborators.</summary>
public static class TestShares
{
    public static ShareManager Manager(IObjectsManager objects, IDatasetsManager datasetsManager,
        IOrganizationsManager organizations, IDatasetResolver datasets, IStorageQuota quota, IAuthManager authManager,
        IDatasetAccessResolver access, IBatchTokenGenerator tokens, INameGenerator names, RegistryContext context,
        IFileSystem fileSystem, IScratchFileProvider scratch, TimeProvider time, IEntryCommandService entries,
        IChunkStore? chunks = null, IDdbManager? ddbManager = null)
    {
        var store = new ShareBatchStore(context, authManager, access, tokens, time, NullLogger<ShareBatchStore>.Instance);
        var rollback = new ShareBatchRollback(context, access, store, datasetsManager, entries,
            NullLogger<ShareBatchRollback>.Instance);
        var targets = new ShareTargets(context, datasets, organizations, datasetsManager, rollback, names,
            NullLogger<ShareTargets>.Instance);
        return new ShareManager(store,
            new ShareBatches(store, rollback, targets, authManager, quota, NullLogger<ShareBatches>.Instance),
            new ShareUploads(store, quota, objects, fileSystem, NullLogger<ShareUploads>.Instance),
            new ShareChunkedUploads(store, quota, chunks ?? new ChunkStore(), scratch, ddbManager ?? Mock.Of<IDdbManager>(),
                objects, NullLogger<ShareChunkedUploads>.Instance));
    }
}
