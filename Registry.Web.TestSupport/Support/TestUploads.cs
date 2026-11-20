using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;

namespace Registry.Web.Test.Support;

/// <summary>
/// An <see cref="UploadManager"/> over its collaborators for tests that build the upload pipeline by hand
/// (the manager takes the services it was split into; this wires them the way <c>AddUploadPipeline</c> does).
/// Without a receipt store, an index queue or a thumbnail queue the pipeline
/// keeps no receipts, indexes with <c>AddRaw</c> and pre-generates no thumbnail.
/// </summary>
public static class TestUploads
{
    public static UploadManager Create(IAuthManager auth, IDatasetAccessResolver access, IFileSystem fs,
        IOptions<ThumbnailOptions> thumbnailOptions, IOptions<ImportSettings> importSettings, BuildScheduler builds,
        UploadQuotaGuard quota, IDatasetChangeCoalescer changes, DistributedUploadReceiptStore? receipts = null,
        IDatasetIndexQueue? indexQueue = null, ThumbnailPrecomputeQueue? thumbnails = null)
    {
        var files = new UploadFiles(TestPaths.Policy, importSettings, TestScratch.Provider, fs,
            NullLogger<UploadFiles>.Instance);
        var tracker = new UploadReceiptTracker(NullLogger<UploadReceiptTracker>.Instance, receipts);
        var indexer = new UploadIndexer(NullLogger<UploadIndexer>.Instance, indexQueue);
        var finalizer = new UploadFinalizer(builds, changes, thumbnailOptions, thumbnails);
        var committer = new UploadCommitter(files, quota, tracker, indexer, finalizer,
            NullLogger<UploadCommitter>.Instance);

        return new UploadManager(auth, access, quota, tracker, indexer, files, committer);
    }

    /// <summary>
    /// The quota guard of the upload pipeline over <paramref name="ledger"/> and <paramref name="quota"/>, counting the
    /// open chunked uploads in <paramref name="store"/> (a new one without) and the organizations in
    /// <paramref name="context"/> (a new empty database without).
    /// </summary>
    public static UploadQuotaGuard Guard(UploadQuotaLedger ledger, IStorageQuota quota, ChunkStore? store = null,
        RegistryContext? context = null) =>
        new(ledger, quota, store ?? new ChunkStore(), context ?? TestDb.CreateContext<RegistryContext>());
}
