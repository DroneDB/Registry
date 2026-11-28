namespace Registry.Web.Models.Configuration;

/// <summary>
/// Tuning for the per-dataset index write queue (<c>IDatasetIndexQueue</c>). Bound from the
/// <c>AppSettings:IndexQueue</c> section.
/// </summary>
public class IndexQueueSettings
{
    /// <summary>
    /// How long to wait for stragglers before committing a batch. Kept small: under real
    /// concurrency, requests already pipeline behind the previous batch's commit, so this is
    /// only a burst-detection window, not the dominant source of latency.
    /// </summary>
    public int BatchWindowMs { get; set; } = 20;

    /// <summary>Maximum number of paths per native batch call.</summary>
    public int MaxBatchSize { get; set; } = 64;

    /// <summary>Per-dataset backpressure threshold (bounded channel capacity).</summary>
    public int QueueCapacityPerDataset { get; set; } = 512;

    /// <summary>
    /// Caller-side deadline before a transient failure is returned. Longer than a busy batch can take to fail: libddb
    /// retries a locked index for 60 s (its retry deadline), then one more wait of up to 2 s and one attempt of up to
    /// the 200 ms busy timeout, about 62 s, and the queue retries the batch once, so a batch on an index locked for
    /// longer fails after about 125 s. With 150 s that failure, or a success, reaches the caller before the deadline in
    /// the common case, and the file is withdrawn or listed while the request still waits; 120 s always ended first.
    /// </summary>
    public int EnqueueTimeoutSeconds { get; set; } = 150;

    /// <summary>
    /// Seconds a per-dataset lane may stay idle (no queued work, no enqueue activity) before
    /// it is released and its memory reclaimed. New enqueues transparently recreate the lane.
    /// Must be in (0, 86400] (validated at startup).
    /// </summary>
    public int IdleLaneTrimSeconds { get; set; } = 1800;
}
