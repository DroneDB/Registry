#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Outcome of one <see cref="IndexedJobPublication.PublishAsync"/> attempt.
/// </summary>
internal enum IndexedJobPublicationOutcome
{
    /// <summary>This caller moved the job from <c>Indexing</c> to its destination state.</summary>
    Published,

    /// <summary>The job had already left <c>Indexing</c> (published by another caller, cancelled, ...).</summary>
    AlreadyLeftIndexing,

    /// <summary>The transition was rejected but the job is still in <c>Indexing</c>.</summary>
    StillIndexing,

    /// <summary>The JobIndex row of this publication has a different identity; no transition was attempted.</summary>
    Incompatible
}

/// <param name="Outcome">What happened.</param>
/// <param name="ActualState">Hangfire state re-read after a rejected transition (null if missing or not re-read).</param>
internal sealed record IndexedJobPublicationResult(IndexedJobPublicationOutcome Outcome, string? ActualState = null);

/// <summary>
/// Publishes one indexed job from its <c>Indexing</c> state: raw metadata parameters, JobIndex
/// preparation, then the expected-state transition to the recorded destination. Every step is
/// idempotent and safe to run concurrently, so the publisher and any number of recovery passes can
/// publish the same job without a lock; only one transition can win. Exceptions propagate (callers
/// treat them as transient); nothing here cancels or resets a job.
/// </summary>
internal static class IndexedJobPublication
{
    public static async Task<IndexedJobPublicationResult> PublishAsync(string jobId,
        IndexedJobPublicationRecord record, JobStorage storage, IBackgroundJobClient client,
        IJobIndexWriter writer, string? methodDisplay, ILogger log, CancellationToken ct = default)
    {
        var meta = record.Payload;

        // Raw strings with the historical blank-skipping: BuildRetrySchedulerFilter and the pending
        // coordinator read these parameters directly.
        using (var conn = storage.GetConnection())
        {
            conn.SetJobParameter(jobId, JobParamKeys.OrgSlug, meta.OrgSlug);
            conn.SetJobParameter(jobId, JobParamKeys.DsSlug, meta.DsSlug);
            if (!string.IsNullOrWhiteSpace(meta.Path)) conn.SetJobParameter(jobId, JobParamKeys.Path, meta.Path);
            if (!string.IsNullOrWhiteSpace(meta.Hash)) conn.SetJobParameter(jobId, JobParamKeys.Hash, meta.Hash);
            if (!string.IsNullOrWhiteSpace(meta.UserId)) conn.SetJobParameter(jobId, JobParamKeys.UserId, meta.UserId);
            if (!string.IsNullOrWhiteSpace(meta.Queue)) conn.SetJobParameter(jobId, JobParamKeys.Queue, meta.Queue);
        }

        var preparation = await writer.PrepareForPublicationAsync(jobId, meta, record.CreatedAtUtc, methodDisplay, ct);
        if (preparation == JobIndexPreparationResult.Incompatible)
            return new IndexedJobPublicationResult(IndexedJobPublicationOutcome.Incompatible);

        if (client.ChangeState(jobId, record.CreateDestinationState(), IndexingState.StateName))
            return new IndexedJobPublicationResult(IndexedJobPublicationOutcome.Published);

        // Rejected: someone else published or cancelled it (possibly a transition whose response was
        // lost). A job that left Indexing counts as published and must not be touched again.
        string? actual;
        using (var conn = storage.GetConnection())
            actual = conn.GetStateData(jobId)?.Name;

        if (actual == IndexingState.StateName)
        {
            log.LogWarning("Indexed job {JobId} is still in {State} after a rejected publication transition",
                jobId, IndexingState.StateName);
            return new IndexedJobPublicationResult(IndexedJobPublicationOutcome.StillIndexing, actual);
        }

        log.LogDebug("Indexed job {JobId} already left {Indexing} (now {State})", jobId,
            IndexingState.StateName, actual ?? "<missing>");
        return new IndexedJobPublicationResult(IndexedJobPublicationOutcome.AlreadyLeftIndexing, actual);
    }
}
