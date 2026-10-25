#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;
using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>The share API (a facade over the batch, rollback, target and upload services) over test collaborators.</summary>
public static class TestShares
{
    public static ShareManager Manager(IObjectsManager objects, IDatasetsManager datasetsManager,
        IOrganizationsManager organizations, IDatasetResolver datasets, IStorageQuota quota, IAuthManager authManager,
        IDatasetAccessResolver access, IBatchTokenGenerator tokens, INameGenerator names, RegistryContext context,
        IFileSystem fileSystem, ScratchFileProvider scratch, TimeProvider time, IEntryCommandService entries,
        ChunkStore? chunks = null, IDdbManager? ddbManager = null)
    {
        var store = new ShareBatchStore(context, authManager, access, tokens, time, NullLogger<ShareBatchStore>.Instance);
        var rollback = new ShareBatchRollback(context, access, store, datasetsManager, entries,
            NullLogger<ShareBatchRollback>.Instance);
        var targets = new ShareTargets(context, datasets, organizations, datasetsManager, rollback, names,
            NullLogger<ShareTargets>.Instance);
        return new ShareManager(store,
            new ShareBatches(store, rollback, targets, authManager, quota, NullLogger<ShareBatches>.Instance),
            new ShareUploads(store, objects, fileSystem, NullLogger<ShareUploads>.Instance),
            new ShareChunkedUploads(store, quota, chunks ?? new ChunkStore(), scratch, ddbManager ?? Mock.Of<IDdbManager>(),
                objects, NullLogger<ShareChunkedUploads>.Instance));
    }

    /// <summary>
    /// Uploads a file of known content to a running batch through the path the controllers use: the
    /// upload target, a temporary file on the dataset's volume, then the commit.
    /// </summary>
    public static async Task<UploadResultDto> Upload(this ShareManager shares, IObjectsManager objects, string token,
        string path, byte[] data)
    {
        var (orgSlug, dsSlug) = await shares.GetUploadTarget(token);
        await using var stream = new MemoryStream(data);
        var file = await objects.StreamToTempAsync(orgSlug, dsSlug, stream);
        return await shares.UploadStreamed(token, path, file);
    }
}
