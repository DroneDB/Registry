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
/// Default <see cref="IThumbnailPrecomputeQueue"/>: a bounded channel drained by
/// <see cref="UploadSettings.ThumbnailPrecomputeWorkers"/> workers, which start with the first
/// request. The thumbnail goes through <see cref="ICacheManager"/> exactly like an on-demand one,
/// so a view that arrives first simply joins (or replaces) the pre-generation.
/// </summary>
public sealed class ThumbnailPrecomputeQueue : IThumbnailPrecomputeQueue, IHostedService, IDisposable
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
        IOptions<AppSettings> settings, ILogger<ThumbnailPrecomputeQueue> logger)
        : this(cache, generator, settings.Value.Uploads ?? new UploadSettings(), logger)
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
            await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(r.OrgSlug, r.DsSlug), r.Hash, r.Size,
                new Func<Task<byte[]>>(async () =>
                {
                    using var s = new MemoryStream();
                    await _generator.GenerateThumbnailAsync(r.LocalPath, r.Size, s);
                    return s.ToArray();
                }));
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
