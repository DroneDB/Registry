using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Uploads;

/// <summary>What follows the index step of an upload (out of <c>UploadManager</c>): builds, thumbnails, cache invalidation.</summary>
public sealed class UploadFinalizer
{
    private readonly BuildScheduler _builds;
    private readonly IDatasetChangeCoalescer _changes;
    private readonly int _defaultThumbnailSize;
    private readonly ThumbnailPrecomputeQueue? _thumbnails;

    public UploadFinalizer(BuildScheduler builds, IDatasetChangeCoalescer changes,
        IOptions<ThumbnailOptions> thumbnailOptions, ThumbnailPrecomputeQueue? thumbnails = null)
    {
        _builds = builds;
        _changes = changes;
        _defaultThumbnailSize = thumbnailOptions.Value.DefaultThumbnailSize;
        _thumbnails = thumbnails;
    }

    /// <summary>
    /// After the files are indexed: schedule builds and thumbnails, then invalidate the caches that depend on the
    /// entry set once for the request.
    /// </summary>
    public async Task FinalizeAsync(UploadRequestContext ctx, IReadOnlyList<MovedUploadPart> parts)
    {
        if (parts.Count == 0)
            return;

        // The buildable parts are built (one manifest call), and the pending builds that waited for a part retried;
        // the others get their thumbnail
        var queued = await _builds.QueueAddedAsync(new BuildTarget(ctx.Ddb, ctx.Key, ctx.OrgSlug, ctx.DsSlug),
            [.. parts.Select(p => p.Entry!.Path)], new BuildOrigin(ctx.User?.Id));
        var built = queued.Select(b => b.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var part in parts.Where(p => !built.Contains(p.Entry!.Path)))
        {
            if (part.Entry!.Type is not (EntryType.Image or EntryType.GeoImage) || _thumbnails == null)
                continue;

            _thumbnails.TryEnqueue(new ThumbnailPrecomputeRequest(ctx.OrgSlug, ctx.DsSlug, part.Entry.FileHash(),
                part.LocalPath, _defaultThumbnailSize));
        }

        // OGC documents and the dataset thumbnail: once per dataset per window, not per file
        _changes.FilesAdded(ctx.OrgSlug, ctx.DsSlug, parts.Select(p => p.Part.Path));
    }
}
