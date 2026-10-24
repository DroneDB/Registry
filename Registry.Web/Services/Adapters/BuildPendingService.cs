#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Service for processing pending builds across all datasets.
/// Uses cache optimization to reduce unnecessary filesystem scans.
/// </summary>
/// <remarks>
/// The recurring sweep (<see cref="ProcessPendingBuilds(PerformContext, CancellationToken)"/>) is not
/// retried: it runs again later. Its success records are noise and expire quickly. One sweep at a
/// time across the hosts: the per-dataset coordinator lock keeps two from enqueueing twice, but
/// both would scan every dataset. Timeout 0: the sweep in progress does the work.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
[JobExpiration(ExpirationTimeoutInMinutes = 60)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class BuildPendingService : Builds.IPendingBuildFlag
{
    private readonly DatasetSweep _sweep;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly ICacheManager _cacheManager;
    private readonly ILogger<BuildPendingService> _logger;
    private readonly JobStorage _storage;
    private readonly string? _mysqlConnectionString;

    // Statistics tracking
    private DateTime? _lastRun;
    private ProcessingStats _lastStats = new();
    private long _lastDurationMs;
    private readonly Lock _statsLock = new();

    public BuildPendingService(
        DatasetSweep sweep,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        ICacheManager cacheManager,
        JobStorage storage,
        ILogger<BuildPendingService> logger,
        IConfiguration? configuration = null)
    {
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _backgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _mysqlConnectionString = configuration?.GetConnectionString(MagicStrings.HangfireConnectionName);
    }

    // Observations remain cacheable. Retry controls live in shared Hangfire storage
    // so host-local caches and category invalidation cannot split/reset a retry chain.
    // The old flat cache fields are imported once, then mirrored for compatibility.
    private async Task<CacheState> ReadState(string orgSlug, string dsSlug, JobStorage? storage = null)
    {
        byte[]? cached = null;
        try
        {
            cached = await _cacheManager.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed,
                ForDataset(orgSlug, dsSlug));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pending observation cache unavailable for {Org}/{Ds}; using shared controls and native state",
                orgSlug, dsSlug);
        }
        var state = new CacheState();
        if (cached is { Length: > 0 })
        {
            try { state = DeserializeCacheState(cached); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Malformed pending cache for {Org}/{Ds}; checking native state", orgSlug, dsSlug);
            }
        }

        using var connection = (storage ?? _storage).GetConnection();
        var controls = PendingBuildRetryControlStore.Load(connection,
            DatasetBuildCoordinator.RetryStateKey(orgSlug, dsSlug), cached, _logger);
        state.RetryAttempt = controls.RetryAttempt;
        state.ScheduledRetryJobId = controls.ScheduledRetryJobId;
        state.RetryEpoch = controls.RetryEpoch;
        state.LegacyImportComplete = controls.LegacyImportComplete;
        state.RetryControlsVersion = 1;
        return state;
    }

    private async Task SaveState(string orgSlug, string dsSlug, CacheState state, JobStorage? storage = null)
    {
        using (var connection = (storage ?? _storage).GetConnection())
            PendingBuildRetryControlStore.Save(connection, DatasetBuildCoordinator.RetryStateKey(orgSlug, dsSlug),
                new PendingBuildRetryControlStore.Controls
                {
                    RetryAttempt = state.RetryAttempt,
                    ScheduledRetryJobId = state.ScheduledRetryJobId,
                    RetryEpoch = state.RetryEpoch,
                    LegacyImportComplete = state.LegacyImportComplete
                });
        // Publish shared controls first: an unavailable observation cache must not
        // erase the budget or permit a second coordinator after a successful enqueue.
        await _cacheManager.SetAsync(MagicStrings.BuildPendingTrackerCacheSeed,
            ForDataset(orgSlug, dsSlug), SerializeCacheState(state));
    }

    private string? GetStampChecksum(IDDB ddb, string orgSlug, string dsSlug)
    {
        try { return ddb.GetStamp()?.Checksum; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot get stamp for {Org}/{Ds}; checking pending state", orgSlug, dsSlug);
            return null;
        }
    }

    private static bool ShouldCheckDataset(CacheState state, string? checksum)
    {
        if (state.HasPending || state.RetryAttempt != 0 || state.ScheduledRetryJobId != null ||
            string.IsNullOrEmpty(checksum) || state.StampChecksum != checksum)
            return true;
        try
        {
            var age = DateTime.UtcNow - DateTime.FromBinary(state.LastCheckBinary);
            return age < TimeSpan.Zero || age > TimeSpan.FromHours(6);
        }
        catch (ArgumentException) { return true; }
    }

    private static void Observe(CacheState state, bool hasPending, string? checksum)
    {
        state.HasPending = hasPending;
        state.StampChecksum = checksum;
        state.LastCheckBinary = DateTime.UtcNow.ToBinary();
    }

    private static void ClearRetryControls(CacheState state)
    {
        state.RetryAttempt = 0;
        state.ScheduledRetryJobId = null;
        state.RetryEpoch = null;
        // Authoritative no-pending ends migration too: stale host observations
        // must not resurrect a retry chain that native state already completed.
        state.LegacyImportComplete = true;
    }

    private bool HasLiveCoordinator(CacheState state, string? terminalJobId = null)
    {
        if (string.IsNullOrEmpty(state.ScheduledRetryJobId) || state.ScheduledRetryJobId == terminalJobId)
            return false;
        var status = _backgroundJob.GetJobStatus(state.ScheduledRetryJobId);
        return status is JobStatus.Scheduled or JobStatus.Enqueued or JobStatus.Processing or JobStatus.Awaiting;
    }

    /// <summary>
    /// Updates the upload-side observation without erasing an existing retry chain.
    /// Failures are logged and do not fail the upload.
    /// </summary>
    /// <returns>A task representing the observation update.</returns>
    public async Task SetDatasetHasPendingBuilds(string orgSlug, string dsSlug, bool hasPending)
    {
        try
        {
            using var coordination = DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, _storage, mysqlConnectionString: _mysqlConnectionString);
            var state = await ReadState(orgSlug, dsSlug);
            // Uploads invalidate the stamp observation, including companion uploads.
            Observe(state, hasPending, null);
            await SaveState(orgSlug, dsSlug, state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update pending observation for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }

    /// <summary>
    /// Refreshes native pending observations after a failed job while preserving
    /// the budget and coordinator scheduled by another state filter.
    /// </summary>
    /// <returns>A task representing the observation refresh.</returns>
    public async Task RefreshPendingStatus(string orgSlug, string dsSlug)
        => await RefreshPendingStatus(orgSlug, dsSlug, _storage);

    internal async Task RefreshPendingStatus(string orgSlug, string dsSlug, JobStorage? storage)
    {
        using var coordination = DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, storage ?? _storage,
            mysqlConnectionString: _mysqlConnectionString);
        var state = await ReadState(orgSlug, dsSlug, storage);
        var ds = await _sweep.FindAsync(orgSlug, dsSlug);
        if (ds?.Organization == null) return;
        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        try
        {
            var hasPending = ddb.IsBuildPending();
            Observe(state, hasPending, GetStampChecksum(ddb, orgSlug, dsSlug));
            if (!hasPending) ClearRetryControls(state);
        }
        catch (Exception ex)
        {
            // Force the sweep to re-read disk without deleting retry controls.
            Observe(state, true, null);
            _logger.LogWarning(ex, "Cannot refresh pending status for {Org}/{Ds}", orgSlug, dsSlug);
        }
        await SaveState(orgSlug, dsSlug, state, storage);
    }

    // Keep the existing bounded policy; exhausted chains await the low-frequency sweep.
    private static readonly int[] RetryBackoffMinutes = [1, 2, 5, 10, 20, 40, 60];
    private const int MaxRetryAttempts = PendingBuildRetryControlStore.MaxRetryAttempts;

    /// <summary>
    /// Schedules one bounded delayed coordinator when native pending work remains.
    /// </summary>
    /// <returns>A task representing the scheduling attempt.</returns>
    public Task ScheduleRetryIfPending(string orgSlug, string dsSlug) =>
        ScheduleRetryIfPending(orgSlug, dsSlug, null, null, null);

    // A terminal job's state is still Processing in storage until OnStateApplied's
    // transaction commits. Exclude precisely that ID from live-coordinator dedup.
    // Failed entry jobs additionally require their own path/hash pending marker.
    internal async Task ScheduleRetryIfPending(string orgSlug, string dsSlug, string? terminalJobId,
        string? requiredPendingPath, string? requiredPendingHash, JobStorage? storage = null)
    {
        try
        {
            using var coordination = DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, storage ?? _storage,
            mysqlConnectionString: _mysqlConnectionString);
            var ds = await _sweep.FindAsync(orgSlug, dsSlug);
            if (ds?.Organization == null) return;
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            if (requiredPendingPath != null &&
                (string.IsNullOrWhiteSpace(requiredPendingPath) || string.IsNullOrWhiteSpace(requiredPendingHash) ||
                 !(ddb.GetPendingBuildInfo() ?? []).Any(p =>
                     p.Path == requiredPendingPath && p.Hash == requiredPendingHash)))
                return;

            var hasPending = ddb.IsBuildPending();
            var state = await ReadState(orgSlug, dsSlug, storage);
            Observe(state, hasPending, GetStampChecksum(ddb, orgSlug, dsSlug));
            if (!hasPending)
            {
                ClearRetryControls(state);
                await SaveState(orgSlug, dsSlug, state, storage);
                return;
            }

            if (HasLiveCoordinator(state, terminalJobId))
            {
                await SaveState(orgSlug, dsSlug, state, storage);
                return;
            }

            state.ScheduledRetryJobId = null;
            if (state.RetryAttempt >= MaxRetryAttempts)
            {
                await SaveState(orgSlug, dsSlug, state, storage);
                _logger.LogInformation("Pending retry budget exhausted for {Org}/{Ds}; awaiting safety-net sweep",
                    orgSlug, dsSlug);
                return;
            }

            var delayMinutes = RetryBackoffMinutes[Math.Min(state.RetryAttempt, RetryBackoffMinutes.Length - 1)];
            var meta = new IndexPayload(orgSlug, dsSlug, null, MagicStrings.AutoBuildServiceUserId);
            var jobId = _backgroundJob.ScheduleBuildPending(new DatasetKey(orgSlug, ds.InternalRef), meta,
                TimeSpan.FromMinutes(delayMinutes));
            if (string.IsNullOrWhiteSpace(jobId))
                throw new InvalidOperationException("Pending retry schedule returned no job ID");
            state.RetryEpoch ??= Guid.NewGuid().ToString("N");
            state.RetryAttempt += 1;
            state.ScheduledRetryJobId = jobId;
            await SaveState(orgSlug, dsSlug, state, storage);
            _logger.LogInformation("Scheduled pending retry for {Org}/{Ds} in {Delay}m (attempt {Attempt}/{Max}): {JobId}",
                orgSlug, dsSlug, delayMinutes, state.RetryAttempt, MaxRetryAttempts, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduleRetryIfPending failed for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }

    /// <summary>
    /// Gets the current statistics for the build pending processing job.
    /// </summary>
    public Models.DTO.BuildPendingStatusDto GetStatus()
    {
        lock (_statsLock)
        {
            return new Models.DTO.BuildPendingStatusDto
            {
                LastRun = _lastRun,
                TotalDatasets = _lastStats.TotalProcessed,
                DatasetsChecked = _lastStats.Checked,
                DatasetsSkipped = _lastStats.Skipped,
                PendingBuildsFound = _lastStats.PendingFound,
                JobsEnqueued = _lastStats.JobsEnqueued,
                Errors = _lastStats.Errors,
                DurationMs = _lastDurationMs,
                IsEnabled = true
            };
        }
    }

    /// <summary>The job as stored before RQ-09 (D4): runs with the context's shutdown token.</summary>
    public Task ProcessPendingBuilds(PerformContext? context) => ProcessPendingBuilds(context, JobCancellation.Of(context));

    /// <summary>
    /// Low-frequency safety-net sweep that scans every dataset for pending builds.
    /// Day-to-day retries are event-driven (see <see cref="ScheduleRetryIfPending"/>);
    /// this recurring job only backstops retries lost to restarts or left over from
    /// before the event-driven mechanism existed.
    /// </summary>
    public async Task ProcessPendingBuilds(PerformContext? context, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var console = JobConsole.For(_logger, context);

        console.Info("Starting ProcessPendingBuilds job");

        var stats = new ProcessingStats();
        try
        {
            var result = await _sweep.ForEachAsync(console, "Pending build check", async (ds, _) =>
            {
                var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);

                using var coordination = DatasetBuildCoordinator.Acquire(ds.OrgSlug, ds.DsSlug, _storage,
                    mysqlConnectionString: _mysqlConnectionString);
                var state = await ReadState(ds.OrgSlug, ds.DsSlug);
                var stampChecksum = GetStampChecksum(ddb, ds.OrgSlug, ds.DsSlug);
                if (!ShouldCheckDataset(state, stampChecksum))
                {
                    stats.Skipped++;
                    return;
                }
                stats.Checked++;

                bool hasPending;
                try { hasPending = ddb.IsBuildPending(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cannot check pending for {Org}/{Ds}; attempting bounded recovery",
                        ds.OrgSlug, ds.DsSlug);
                    hasPending = true;
                    stampChecksum = null;
                }
                Observe(state, hasPending, stampChecksum);
                if (!hasPending)
                {
                    ClearRetryControls(state);
                    await SaveState(ds.OrgSlug, ds.DsSlug, state);
                    return;
                }
                stats.PendingFound++;

                if (HasLiveCoordinator(state))
                {
                    await SaveState(ds.OrgSlug, ds.DsSlug, state);
                    return;
                }

                // Restart one bounded epoch only after the previous coordinator
                // is missing/terminal, irrespective of an unchanged database stamp.
                var meta = new IndexPayload(ds.OrgSlug, ds.DsSlug, null, MagicStrings.AutoBuildServiceUserId);
                var jobId = _backgroundJob.EnqueueBuildPending(new DatasetKey(ds.OrgSlug, ds.InternalRef), meta);
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException("Pending coordinator enqueue returned no job ID");
                state.RetryAttempt = 0;
                state.RetryEpoch = Guid.NewGuid().ToString("N");
                state.ScheduledRetryJobId = jobId;
                await SaveState(ds.OrgSlug, ds.DsSlug, state);
                stats.JobsEnqueued++;
                _logger.LogInformation("Started pending recovery epoch for {Org}/{Ds}: {JobId}",
                    ds.OrgSlug, ds.DsSlug, jobId);
            }, cancellationToken);

            stats.TotalProcessed = result.Datasets;
            stats.Errors = result.Failed;

            // The server log gets the summary at information level only when something was found
            var summary = "ProcessPendingBuilds completed: Total={Total}, Checked={Checked}, Skipped={Skipped}, " +
                          "PendingFound={PendingFound}, JobsEnqueued={JobsEnqueued}, Errors={Errors}";
            object?[] values = [stats.TotalProcessed, stats.Checked, stats.Skipped, stats.PendingFound,
                stats.JobsEnqueued, stats.Errors];
            if (stats.PendingFound > 0 || stats.Errors > 0)
                console.Info(summary, values);
            else
                console.Debug(summary, values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fatal error in ProcessPendingBuilds job");
            throw; // Re-throw to let Hangfire handle it
        }
        finally
        {
            sw.Stop();
            lock (_statsLock)
            {
                _lastRun = startTime;
                _lastStats = stats;
                _lastDurationMs = sw.ElapsedMilliseconds;
            }
        }
    }

    // Helper classes and methods

    private class CacheState
    {
        public bool HasPending { get; set; }
        public long LastCheckBinary { get; set; }
        public string? StampChecksum { get; set; }

        // Legacy mirrors only; shared Hangfire controls are authoritative.
        public int RetryAttempt { get; set; }
        public string? ScheduledRetryJobId { get; set; }
        public string? RetryEpoch { get; set; }
        public int RetryControlsVersion { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool LegacyImportComplete { get; set; }
    }

    private class ProcessingStats
    {
        public int TotalProcessed { get; set; }
        public int Checked { get; set; }
        public int Skipped { get; set; }
        public int PendingFound { get; set; }
        public int JobsEnqueued { get; set; }
        public int Errors { get; set; }
    }

    private static byte[] SerializeCacheState(CacheState state)
    {
        var json = JsonSerializer.Serialize(state);
        return Encoding.UTF8.GetBytes(json);
    }

    private static CacheState DeserializeCacheState(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<CacheState>(json)
               ?? throw new InvalidOperationException("Failed to deserialize cache state");
    }
}
