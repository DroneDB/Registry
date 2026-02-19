using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// In-memory implementation of <see cref="IDownloadLimiter"/> using atomic operations.
/// Registered as singleton to share state across all requests.
/// </summary>
public class DownloadLimiter : IDownloadLimiter
{
    private readonly int? _maxConcurrentDownloads;
    private readonly ConcurrentDictionary<string, int> _activeDownloads = new();
    private readonly ILogger<DownloadLimiter> _logger;

    public DownloadLimiter(IOptions<AppSettings> settings, ILogger<DownloadLimiter> logger)
    {
        _maxConcurrentDownloads = settings.Value.MaxConcurrentDownloadsPerUser;
        _logger = logger;

        if (IsEnabled)
            _logger.LogInformation("Download limiter enabled: max {Max} concurrent downloads per user",
                _maxConcurrentDownloads);
        else
            _logger.LogInformation("Download limiter is disabled (MaxConcurrentDownloadsPerUser is null)");
    }

    /// <inheritdoc />
    public bool IsEnabled => _maxConcurrentDownloads is > 0;

    /// <inheritdoc />
    public bool TryAcquireSlot(string key)
    {
        if (!IsEnabled) return true;

        // Atomically increment, then check if over limit
        var newCount = _activeDownloads.AddOrUpdate(key, 1, (_, current) => current + 1);

        if (newCount <= _maxConcurrentDownloads)
        {
            _logger.LogDebug("Download slot acquired for '{Key}': {Count}/{Max}",
                key, newCount, _maxConcurrentDownloads);
            return true;
        }

        // Over limit — roll back the increment
        DecrementSafe(key);

        _logger.LogWarning("Download limit reached for '{Key}': {Active}/{Max}",
            key, _maxConcurrentDownloads, _maxConcurrentDownloads);
        return false;
    }

    /// <inheritdoc />
    public void ReleaseSlot(string key)
    {
        if (!IsEnabled) return;

        var newCount = DecrementSafe(key);

        _logger.LogDebug("Download slot released for '{Key}': {Count}/{Max}",
            key, newCount, _maxConcurrentDownloads);

        // Clean up entries that reach 0 to avoid unbounded dictionary growth
        if (newCount <= 0)
        {
            _activeDownloads.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public int GetActiveDownloads(string key)
    {
        return _activeDownloads.TryGetValue(key, out var count) ? count : 0;
    }

    /// <inheritdoc />
    public bool CanAcquireSlot(string key)
    {
        if (!IsEnabled) return true;
        return GetActiveDownloads(key) < _maxConcurrentDownloads;
    }

    /// <summary>
    /// Atomically decrements the counter for the given key, never going below 0.
    /// </summary>
    private int DecrementSafe(string key)
    {
        // Use AddOrUpdate with a decrement function that clamps at 0
        return _activeDownloads.AddOrUpdate(key, 0, (_, current) => Math.Max(0, current - 1));
    }
}
