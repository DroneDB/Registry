using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Web.Attributes;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The recurring JobIndex state synchronization: reconciles JobIndex rows still in
/// <see cref="SyncedStates"/> with the live state of their job in the configured Hangfire storage.
/// </summary>
/// <remarks>
/// One run at a time across the hosts: an hourly run over many rows can outlast the hour, and two
/// would read and write the same rows (the writes are guarded, the work is wasted). Timeout 0: the
/// run in progress does the work.
/// </remarks>
[JobExpiration(ExpirationTimeoutInMinutes = 60)]
[AutomaticRetry(Attempts = 1)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class JobIndexSyncService
{
    /// <summary>
    /// JobIndex row states that can lag behind Hangfire and are reconciled from the live job state:
    /// the legacy initial state and the initial state of an indexed job publication.
    /// </summary>
    internal static readonly string[] SyncedStates = ["Created", IndexingState.StateName];

    private readonly IJobIndexQuery _jobIndexQuery;
    private readonly IJobIndexWriter _jobIndexWriter;
    private readonly JobStorage _storage;
    private readonly ILogger<JobIndexSyncService> _logger;

    public JobIndexSyncService(IJobIndexQuery jobIndexQuery, IJobIndexWriter jobIndexWriter, JobStorage storage,
        ILogger<JobIndexSyncService> logger)
    {
        _jobIndexQuery = jobIndexQuery ?? throw new ArgumentNullException(nameof(jobIndexQuery));
        _jobIndexWriter = jobIndexWriter ?? throw new ArgumentNullException(nameof(jobIndexWriter));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The job as stored by earlier releases: runs with the context's shutdown token.</summary>
    public Task SyncJobIndexStates(PerformContext? context) => SyncJobIndexStates(context, JobCancellation.Of(context));

    public async Task SyncJobIndexStates(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        console.Info("Starting JobIndex state synchronization");

        try
        {
            var result = await SyncAsync(cancellationToken);
            console.Info("JobIndex state synchronization completed. Updated: {Updated}, Not found: {NotFound}, " +
                         "Changed concurrently: {Skipped}", result.Updated, result.NotFound, result.Skipped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Error(ex, "Error during JobIndex state synchronization: {Error}", ex.Message);
            throw;
        }
    }

    /// <summary>Counts of a synchronization pass.</summary>
    internal readonly record struct SyncResult(int Updated, int NotFound, int Skipped);

    /// <summary>
    /// Reconciles JobIndex rows still in <see cref="SyncedStates"/> with the live state of their job.
    /// A row is written only when the live state differs, and only while it is still in a synced
    /// state, so a row a concurrent writer already advanced keeps its state and telemetry. A job
    /// missing from Hangfire is Deleted.
    /// </summary>
    internal async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var rows = new List<Data.Models.JobIndex>();
        foreach (var state in SyncedStates)
        {
            var found = await _jobIndexQuery.GetByStateAsync(state, ct: ct);
            _logger.LogInformation("Found {Count} JobIndex entries with '{State}' state", found.Length, state);
            rows.AddRange(found);
        }

        var monitoringApi = _storage.GetMonitoringApi();
        var updated = 0;
        var notFound = 0;
        var skipped = 0;

        foreach (var jobIndex in rows)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var jobDetails = monitoringApi.JobDetails(jobIndex.JobId);

                if (jobDetails?.History != null && jobDetails.History.Count > 0)
                {
                    var latestHistoryEntry = jobDetails.History[0];
                    var currentState = latestHistoryEntry.StateName;

                    if (currentState == jobIndex.CurrentState)
                        continue;

                    // The timestamp of Hangfire's state history, not now
                    var stateTimestamp = latestHistoryEntry.CreatedAt;

                    _logger.LogDebug("Updating job {JobId} from '{RowState}' to '{CurrentState}' with timestamp {Timestamp}",
                        jobIndex.JobId, jobIndex.CurrentState, currentState, stateTimestamp);

                    if (await _jobIndexWriter.UpdateStateIfCurrentAsync(jobIndex.JobId, SyncedStates,
                            currentState, stateTimestamp, ct))
                        updated++;
                    else
                        skipped++;
                }
                else
                {
                    // Not in Hangfire any more (deleted or expired): Deleted, at the time it was found missing
                    _logger.LogDebug("Job {JobId} not found in Hangfire, marking as deleted", jobIndex.JobId);

                    if (await _jobIndexWriter.UpdateStateIfCurrentAsync(jobIndex.JobId, SyncedStates,
                            "Deleted", DateTime.UtcNow, ct))
                        notFound++;
                    else
                        skipped++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error processing job {JobId}", jobIndex.JobId);
            }
        }

        return new SyncResult(updated, notFound, skipped);
    }
}
