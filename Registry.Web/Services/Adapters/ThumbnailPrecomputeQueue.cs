#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Pre-generates the thumbnails of uploaded images in the background at the lowest priority,
/// with a fixed number of workers. Replaces one unbounded task per upload.
/// </summary>
public sealed class ThumbnailPrecomputeQueue : IHostedService, IDisposable
{
    private readonly ICacheManager _cache;
    private readonly IThumbnailGenerator _generator;
    private readonly ILogger<ThumbnailPrecomputeQueue> _logger;
    private readonly Channel<ThumbnailPrecomputeRequest> _channel;
    private readonly int _workers;
    private readonly CancellationTokenSource _stop = new();
    private Task[] _running = [];
    private int _started;

    public ThumbnailPrecomputeQueue(ICacheManager cache, IThumbnailGenerator generator,
        IOptions<UploadSettings> settings, ILogger<ThumbnailPrecomputeQueue> logger)
        : this(cache, generator, settings.Value, logger)
    {
    }

    public ThumbnailPrecomputeQueue(ICacheManager cache, IThumbnailGenerator generator, UploadSettings settings,
        ILogger<ThumbnailPrecomputeQueue> logger)
    {
        _cache = cache;
        _generator = generator;
        _logger = logger;
        _workers = Math.Max(1, settings.ThumbnailPrecomputeWorkers);
        _channel = Channel.CreateBounded<ThumbnailPrecomputeRequest>(
            new BoundedChannelOptions(Math.Max(1, settings.ThumbnailPrecomputeCapacity))
            {
                FullMode = BoundedChannelFullMode.Wait, // TryWrite fails instead of waiting
                SingleReader = false,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Queues a thumbnail. Returns false when the queue is full: the thumbnail is then
    /// generated on its first view instead.
    /// </summary>
    public bool TryEnqueue(ThumbnailPrecomputeRequest request)
    {
        EnsureStarted();
        if (_channel.Writer.TryWrite(request))
            return true;

        _logger.LogInformation("Thumbnail pre-generation queue full: '{Path}' will get its thumbnail on first view",
            request.LocalPath);
        return false;
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _running = Enumerable.Range(0, _workers).Select(_ => Task.Run(WorkAsync)).ToArray();
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(_stop.Token))
                await GenerateAsync(request);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Shutting down: pending pre-generations are dropped, views generate them later
        }
    }

    private async Task GenerateAsync(ThumbnailPrecomputeRequest r)
    {
        try
        {
            // The queue's stop token: a pre-generation still running at shutdown stops
            await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(r.OrgSlug, r.DsSlug), _stop.Token,
                r.Hash, r.Size,
                new Func<CancellationToken, Task<byte[]>>(async token =>
                {
                    using var s = new MemoryStream();
                    await _generator.GenerateThumbnailAsync(r.LocalPath, r.Size, s, token);
                    return s.ToArray();
                }));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot pre-generate the thumbnail of '{Path}' (hash {Hash})", r.LocalPath, r.Hash);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _stop.CancelAsync();
        await Task.WhenAll(_running).WaitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void Dispose() => _stop.Dispose();
}
