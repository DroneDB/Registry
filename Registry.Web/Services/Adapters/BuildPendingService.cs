#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Service for processing pending builds across all datasets.
/// Uses cache optimization to reduce unnecessary filesystem scans.
/// </summary>
public class BuildPendingService
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly ICacheManager _cacheManager;
    private readonly ILogger<BuildPendingService> _logger;

    // Statistics tracking
    private DateTime? _lastRun;
    private ProcessingStats _lastStats = new();
    private long _lastDurationMs;
    private readonly object _statsLock = new();

    public BuildPendingService(
        RegistryContext context,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        ICacheManager cacheManager,
        ILogger<BuildPendingService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _backgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Register cache provider with 24-hour expiration for auto-cleanup
        if (!_cacheManager.IsRegistered(MagicStrings.BuildPendingTrackerCacheSeed))
        {
            _cacheManager.Register(
                MagicStrings.BuildPendingTrackerCacheSeed,
                async (object[] _) => Array.Empty<byte>(),
                TimeSpan.FromHours(24)
            );
        }
    }

    /// <summary>
    /// Determines if a dataset should be checked for pending builds based on cache state.
    /// Uses DroneDB stamp checksum to detect if the database has actually changed.
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="dsSlug">Dataset slug</param>
    /// <param name="ddb">DDB instance to get current stamp</param>
    /// <returns>True if the dataset should be checked, false to skip</returns>
    private async Task<bool> ShouldCheckDataset(string orgSlug, string dsSlug, IDDB ddb)
    {
        try
        {
            var cacheKey = $"{orgSlug}/{dsSlug}";
            var cached = await _cacheManager.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed, cacheKey);

            if (cached == null || cached.Length == 0)
            {
                // Never checked before - must check
                return true;
            }

            // Deserialize cache state
            var state = DeserializeCacheState(cached);

            // Get current stamp checksum
            string? currentChecksum = null;
            try
            {
                var stamp = ddb.GetStamp();
                currentChecksum = stamp.Checksum;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to get stamp for {Org}/{Ds}, forcing check",
                    orgSlug, dsSlug);
                // If we can't get stamp, force check for safety
                return true;
            }

            // If checksum hasn't changed and there's no pending, skip check
            if (!string.IsNullOrEmpty(state.StampChecksum) &&
                state.StampChecksum == currentChecksum &&
                !state.HasPending)
            {
                _logger.LogDebug(
                    "Skipping {Org}/{Ds}: checksum unchanged ({Checksum})",
                    orgSlug, dsSlug, currentChecksum);
                return false;
            }

            // If checksum changed, we need to check
            if (!string.IsNullOrEmpty(state.StampChecksum) &&
                state.StampChecksum != currentChecksum)
            {
                _logger.LogDebug(
                    "Checksum changed for {Org}/{Ds}: {OldChecksum} -> {NewChecksum}",
                    orgSlug, dsSlug, state.StampChecksum, currentChecksum);
                return true;
            }

            // If it has pending builds, always check
            if (state.HasPending)
            {
                return true;
            }

            // Calculate age since last check
            var lastCheck = DateTime.FromBinary(state.LastCheckBinary);
            var age = DateTime.UtcNow - lastCheck;

            // Staleness check: force check every 6 hours
            // Also handle clock skew (negative age)
            if (age > TimeSpan.FromHours(6) || age < TimeSpan.Zero)
            {
                _logger.LogDebug(
                    "Staleness check triggered for {Org}/{Ds}: age={Age}, forcing check",
                    orgSlug, dsSlug, age);
                return true;
            }

            // Cache is fresh and no pending - skip check
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error checking pending cache for {Org}/{Ds}, forcing check for safety",
                orgSlug, dsSlug);

            // On error, always check (fail-safe behavior)
            return true;
        }
    }

    /// <summary>
    /// Updates the cache with the current pending status for a dataset.
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="dsSlug">Dataset slug</param>
    /// <param name="hasPending">Whether the dataset has pending builds</param>
    /// <param name="stampChecksum">Current DroneDB stamp checksum</param>
    private async Task UpdatePendingStatus(string orgSlug, string dsSlug, bool hasPending, string? stampChecksum = null)
    {
        try
        {
            var cacheKey = $"{orgSlug}/{dsSlug}";
            var state = new CacheState
            {
                HasPending = hasPending,
                LastCheckBinary = DateTime.UtcNow.ToBinary(),
                StampChecksum = stampChecksum
            };

            var serialized = SerializeCacheState(state);

            await _cacheManager.SetAsync(
                MagicStrings.BuildPendingTrackerCacheSeed,
                cacheKey,
                serialized
            );

            _logger.LogDebug(
                "Updated pending cache for {Org}/{Ds}: HasPending={HasPending}, Checksum={Checksum}",
                orgSlug, dsSlug, hasPending, stampChecksum);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error updating pending cache for {Org}/{Ds}, continuing anyway",
                orgSlug, dsSlug);
            // Non-blocking - cache update failure shouldn't stop processing
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

    /// <summary>
    /// Main recurring job method - processes all datasets for pending builds.
    /// Runs every minute via Hangfire.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)] // Don't retry on failure - will run again next minute
    public async Task ProcessPendingBuilds(PerformContext? context = null)
    {
        var startTime = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Action<string> writeLine = context != null ? context.WriteLine : (msg => _logger.LogInformation(msg));

        writeLine("Starting ProcessPendingBuilds job");

        try
        {
            // Query all datasets with their organizations
            // Use AsNoTracking for read-only performance
            var datasets = await _context.Datasets
                .AsNoTracking()
                .Include(ds => ds.Organization)
                .ToArrayAsync();

            var stats = new ProcessingStats();

            writeLine($"Found {datasets.Length} total datasets to process");

            foreach (var ds in datasets)
            {
                try
                {
                    stats.TotalProcessed++;

                    // Skip if organization is null (safety check)
                    if (ds.Organization == null)
                    {
                        _logger.LogWarning("Dataset {DsSlug} has null organization, skipping", ds.Slug);
                        stats.Errors++;
                        continue;
                    }

                    // Get DDB instance for this dataset
                    IDDB ddb;
                    try
                    {
                        ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to get DDB for {Org}/{Ds}, skipping",
                            ds.Organization.Slug, ds.Slug);
                        stats.Errors++;
                        continue;
                    }

                    // Cache optimization: check if we should skip this dataset
                    // This now uses GetStamp internally to detect database changes
                    if (!await ShouldCheckDataset(ds.Organization.Slug, ds.Slug, ddb))
                    {
                        stats.Skipped++;
                        continue;
                    }

                    stats.Checked++;

                    // Get current stamp for tracking
                    string? stampChecksum = null;
                    try
                    {
                        var stamp = ddb.GetStamp();
                        stampChecksum = stamp.Checksum;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to get stamp checksum for {Org}/{Ds}",
                            ds.Organization.Slug, ds.Slug);
                        // Continue anyway - we'll just cache without checksum
                    }

                    // Check if this dataset has pending builds
                    bool hasPending;
                    try
                    {
                        hasPending = ddb.IsBuildPending();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to check pending status for {Org}/{Ds}, assuming has pending",
                            ds.Organization.Slug, ds.Slug);

                        // On error, assume pending (conservative approach)
                        hasPending = true;
                    }

                    // Update cache with current status and checksum
                    await UpdatePendingStatus(ds.Organization.Slug, ds.Slug, hasPending, stampChecksum);

                    if (!hasPending)
                    {
                        // No pending builds for this dataset
                        continue;
                    }

                    stats.PendingFound++;

                    // Enqueue BuildPending job for this dataset
                    var meta = new IndexPayload(
                        ds.Organization.Slug,
                        ds.Slug,
                        null,
                        "auto-build-service", // Special user ID for automated service
                        null,
                        null
                    );

                    try
                    {
                        var jobId = _backgroundJob.EnqueueIndexed(
                            () => HangfireUtils.BuildPendingWrapper(ddb, null),
                            meta
                        );

                        stats.JobsEnqueued++;

                        _logger.LogInformation(
                            "Enqueued BuildPending job for {Org}/{Ds}: JobId={JobId}",
                            ds.Organization.Slug, ds.Slug, jobId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to enqueue BuildPending job for {Org}/{Ds}",
                            ds.Organization.Slug, ds.Slug);
                        stats.Errors++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error processing dataset {Org}/{Ds}",
                        ds.Organization?.Slug ?? "unknown", ds.Slug);
                    stats.Errors++;
                }
            }

            // Log summary statistics
            var summaryMessage =
                $"ProcessPendingBuilds completed: " +
                $"Total={stats.TotalProcessed}, " +
                $"Checked={stats.Checked}, " +
                $"Skipped={stats.Skipped}, " +
                $"PendingFound={stats.PendingFound}, " +
                $"JobsEnqueued={stats.JobsEnqueued}, " +
                $"Errors={stats.Errors}";

            writeLine(summaryMessage);

            // Only log to standard logger if we found pending or had errors
            if (stats.PendingFound > 0 || stats.Errors > 0)
            {
                _logger.LogInformation(summaryMessage);
            }

            // Update statistics
            sw.Stop();
            lock (_statsLock)
            {
                _lastRun = startTime;
                _lastStats = stats;
                _lastDurationMs = sw.ElapsedMilliseconds;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in ProcessPendingBuilds job");

            // Update statistics even on failure
            sw.Stop();
            lock (_statsLock)
            {
                _lastRun = startTime;
                _lastDurationMs = sw.ElapsedMilliseconds;
            }

            throw; // Re-throw to let Hangfire handle it
        }
    }

    // Helper classes and methods

    private class CacheState
    {
        public bool HasPending { get; set; }
        public long LastCheckBinary { get; set; }
        public string? StampChecksum { get; set; }
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
