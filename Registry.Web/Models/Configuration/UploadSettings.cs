#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>
/// Tuning of the upload pipeline. Bound from <c>AppSettings:Uploads</c>.
/// </summary>
public class UploadSettings
{
    /// <summary>
    /// Background workers that pre-generate the thumbnails of uploaded images (lowest priority;
    /// a thumbnail not pre-generated is generated on first view). Default: 2.
    /// </summary>
    public int ThumbnailPrecomputeWorkers { get; set; } = 2;

    /// <summary>Thumbnails waiting for a worker; further uploads skip pre-generation. Default: 256.</summary>
    public int ThumbnailPrecomputeCapacity { get; set; } = 256;

    /// <summary>
    /// How long the OGC and dataset-thumbnail invalidations of uploaded files are collected before
    /// they run once for the whole burst. Default: 2000 ms.
    /// </summary>
    public int InvalidationWindowMs { get; set; } = 2000;

    /// <summary>
    /// How long one storage-use evaluation serves a user's uploads (reservations of the uploads in
    /// between are added to it). Default: 10 s.
    /// </summary>
    public int QuotaWindowSeconds { get; set; } = 10;

    /// <summary>How long the outcome of an upload can be asked for by its X-Upload-Id. Default: 24 h.</summary>
    public int ReceiptHours { get; set; } = 24;

    /// <summary>Most file parts accepted in one bundled upload request. Default: 64.</summary>
    public int MaxPartsPerRequest { get; set; } = 64;

    /// <summary>Chunked (resumable) uploads: largest chunk accepted. Default: 128 MB.</summary>
    public long MaxChunkBytes { get; set; } = 128L * 1024 * 1024;
}
