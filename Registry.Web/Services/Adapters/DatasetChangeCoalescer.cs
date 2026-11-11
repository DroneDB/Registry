using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IDatasetChangeCoalescer"/>. The first change of a dataset opens a window of
/// <see cref="UploadSettings.InvalidationWindowMs"/>; at its end the dataset's OGC generation is
/// renewed once and, when a thumbnail candidate was among the files, the dataset thumbnail is
/// dropped. Changes during a flush open the next window, so no change is ever left uninvalidated.
/// Pending windows are flushed when the host stops.
/// </summary>
public sealed class DatasetChangeCoalescer : IDatasetChangeCoalescer, IHostedService, IDisposable
{
    private sealed class Pending
    {
        public bool ThumbnailCandidate;
        public ITimer? Timer;
    }

    private readonly IOgcCacheKeys _ogcKeys;
    private readonly ICacheManager _cacheManager;
    private readonly string[] _thumbnailCandidates;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly ILogger<DatasetChangeCoalescer> _logger;
    private readonly ConcurrentDictionary<(string Org, string Ds), Pending> _pending = new();

    public DatasetChangeCoalescer(IOgcCacheKeys ogcKeys, ICacheManager cacheManager, IOptions<ThumbnailOptions> thumbnails,
        IOptions<UploadSettings> uploads, ILogger<DatasetChangeCoalescer> logger)
        : this(ogcKeys, cacheManager, thumbnails.Value.Candidates,
            TimeSpan.FromMilliseconds(uploads.Value.InvalidationWindowMs), logger)
    {
    }

    /// <param name="time">Clock of the invalidation window; <see cref="TimeProvider.System"/> when null.</param>
    public DatasetChangeCoalescer(IOgcCacheKeys ogcKeys, ICacheManager cacheManager, string[] thumbnailCandidates,
        TimeSpan window, ILogger<DatasetChangeCoalescer> logger, TimeProvider? time = null)
    {
        _ogcKeys = ogcKeys;
        _cacheManager = cacheManager;
        _thumbnailCandidates = thumbnailCandidates;
        _window = window < TimeSpan.Zero ? TimeSpan.Zero : window;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public void FilesAdded(string orgSlug, string dsSlug, IEnumerable<string> paths)
    {
        var candidate = paths.Any(p => DatasetThumbnails.IsCandidateFile(p, _thumbnailCandidates));
        var key = (orgSlug, dsSlug);

        while (true)
        {
            var pending = _pending.GetOrAdd(key, _ => new Pending());
            lock (pending)
            {
                // A flush removes the entry under this lock: a removed one is past its window
                if (!_pending.TryGetValue(key, out var current) || !ReferenceEquals(current, pending))
                    continue;

                pending.ThumbnailCandidate |= candidate;
                pending.Timer ??= _time.CreateTimer(_ => _ = FlushAsync(key), null, _window, Timeout.InfiniteTimeSpan);
                return;
            }
        }
    }

    public Task FlushAsync() => Task.WhenAll(_pending.Keys.ToArray().Select(FlushAsync));

    private async Task FlushAsync((string Org, string Ds) key)
    {
        if (!_pending.TryGetValue(key, out var pending))
            return;

        bool thumbnail;
        lock (pending)
        {
            if (!_pending.TryRemove(new KeyValuePair<(string, string), Pending>(key, pending)))
                return; // flushed concurrently
            thumbnail = pending.ThumbnailCandidate;
            pending.Timer?.Dispose();
        }

        try
        {
            await _ogcKeys.InvalidateAsync(key.Org, key.Ds);
            if (thumbnail)
                await _cacheManager.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed,
                    ForDatasetThumbnail(key.Org, key.Ds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot invalidate the caches of {Org}/{Ds} after uploads", key.Org, key.Ds);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => FlushAsync();

    public void Dispose()
    {
        foreach (var pending in _pending.Values)
            pending.Timer?.Dispose();
    }
}
