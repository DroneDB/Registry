#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>
/// Tuning for the per-dataset index write queue (<c>IDatasetIndexQueue</c>). Bound from the
/// <c>AppSettings:IndexQueue</c> section. See ImproveParallelWrites plan, workstream 04 §4.4.
/// </summary>
public class IndexQueueSettings
{
    /// <summary>
    /// How long to wait for stragglers before committing a batch. Kept small: under real
    /// concurrency, requests already pipeline behind the previous batch's commit, so this is
    /// only a burst-detection window, not the dominant source of latency (see plan §4.2 caveat).
    /// </summary>
    public int BatchWindowMs { get; set; } = 20;

    /// <summary>Maximum number of paths per native batch call.</summary>
    public int MaxBatchSize { get; set; } = 64;

    /// <summary>Per-dataset backpressure threshold (bounded channel capacity).</summary>
    public int QueueCapacityPerDataset { get; set; } = 512;

    /// <summary>Caller-side deadline before a transient failure is returned.</summary>
    public int EnqueueTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Seconds a per-dataset lane may stay idle (no queued work, no enqueue activity) before
    /// it is released and its memory reclaimed. New enqueues transparently recreate the lane.
    /// Must be &gt; 0 (validated at startup).
    /// </summary>
    public int IdleLaneTrimSeconds { get; set; } = 1800;
}
