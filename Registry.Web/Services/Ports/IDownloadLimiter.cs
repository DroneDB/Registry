namespace Registry.Web.Services.Ports;

/// <summary>
/// Tracks and limits concurrent downloads per user (or per IP for anonymous users).
/// </summary>
public interface IDownloadLimiter
{
    /// <summary>
    /// Whether the download limiter is active (i.e. MaxConcurrentDownloadsPerUser > 0).
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Tries to acquire a download slot for the given key.
    /// Returns true if the slot was acquired, false if the limit has been reached.
    /// </summary>
    bool TryAcquireSlot(string key);

    /// <summary>
    /// Releases a previously acquired download slot for the given key.
    /// Safe to call even if no slot is held (will not go below 0).
    /// </summary>
    void ReleaseSlot(string key);

    /// <summary>
    /// Gets the number of active downloads for the given key.
    /// </summary>
    int GetActiveDownloads(string key);

    /// <summary>
    /// Checks whether a slot could be acquired for the given key, without actually acquiring it.
    /// Used for preflight checks.
    /// </summary>
    bool CanAcquireSlot(string key);
}
