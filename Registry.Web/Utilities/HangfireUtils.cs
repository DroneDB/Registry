using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Ports;
using Serilog;

namespace Registry.Web.Utilities;

public static class HangfireUtils
{
    private static readonly IFileSystem FileSystem = new FileSystem();

    /// <summary>
    /// Builds the per-job log writer. When <see cref="BuildLogCaptureFilter"/>
    /// has attached a <see cref="LogRingBuffer"/> to the job context, console
    /// output is mirrored into it so the filter can persist it to the JobIndex
    /// when the job finishes. Otherwise it falls back to the Hangfire console
    /// (or Serilog when running outside a job). Every line is scrubbed of
    /// <paramref name="datasetFolderPath"/>: the tail is readable by anonymous users on public datasets.
    /// </summary>
    private static Action<string> CreateJobWriteLine(PerformContext context, string datasetFolderPath)
    {
        var write = CreateRawJobWriteLine(context);
        return msg => write(ScrubDatasetPath(msg, datasetFolderPath));
    }

    private static Action<string> CreateRawJobWriteLine(PerformContext context)
    {
        if (context == null)
            return Log.Information;

        var buffer = context.Items.TryGetValue(BuildLogCaptureFilter.BufferKey, out var raw)
            ? raw as LogRingBuffer
            : null;

        if (buffer == null)
            return context.WriteLine;

        return msg =>
        {
            context.WriteLine(msg);
            lock (buffer)
                buffer.Append(msg);
        };
    }

    /// <summary>
    /// Formats an exception as a single display line for the job log tail:
    /// <c>TypeName: message [InnerTypeName]</c>, message truncated to 500 chars.
    /// Ring-buffer lines are display lines, so newlines are collapsed.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        if (ex == null) return "Unknown error";

        var message = SanitizeSingleLine(ex.Message);
        if (message.Length > 500)
            message = message.Substring(0, 500) + "...";

        // Surfacing the inner type (not its message) keeps the line compact while
        // pinpointing the failure layer, e.g. "DdbException: Cannot open ... [WebException]".
        var inner = ex.InnerException != null ? $" [{ex.InnerException.GetType().Name}]" : string.Empty;

        return $"{ex.GetType().Name}: {message}{inner}";
    }

    // Collapses newlines/CR so an exception message stays a single ring-buffer display line.
    private static string SanitizeSingleLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    // Native messages may use either separator style, so both variants are replaced.
    private static string ScrubDatasetPath(string value, string datasetFolderPath)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(datasetFolderPath)) return value;

        var trimmed = datasetFolderPath.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return value;

        return value
            .Replace(trimmed.Replace('\\', '/'), "[dataset]", StringComparison.OrdinalIgnoreCase)
            .Replace(trimmed.Replace('/', '\\'), "[dataset]", StringComparison.OrdinalIgnoreCase);
    }

    // Transient DDB contention gets a backoff retry chain; anything else fails fast without
    // burning worker slots.
    //
    // NOTE: this exact policy is intentionally re-declared on every job wrapper below rather
    // than extracted once (attribute class/method helper). Hangfire discovers AutomaticRetry via
    // reflection per job method; an indicator attribute is not supported, and a helper would add
    // indirection without a measurable gain. Keeping the policy text inline next to each job is
    // the cost of that discovery model (review round 2, finding 7).
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public static void BuildWrapper(IDDB ddb, string path, bool force,
        PerformContext context)
    {
        Action<string> writeLine = CreateJobWriteLine(context, ddb.DatasetFolderPath);

        writeLine($"In BuildWrapper('{ddb.DatasetFolderPath}', '{path}', '{force}')");

        writeLine("Running build");
        var skipped = false;
        try
        {
            ddb.Build(path, force: force);
        }
        catch (DdbBuildInProgressException ex)
        {
            // Benign: the lock holder is doing the work. Throwing here would trip
            // AutomaticRetry -> Failed -> BuildJobFailureFilter and wrongly invalidate the cache.
            writeLine($"Build lock currently held by another process ({ex.Message}); skipping");
            skipped = true;
        }
        catch (Exception ex)
        {
            // Must land in the JobIndex log tail: without this the Task History shows
            // only "Running build" and admins cannot triage build failures at all.
            // Rethrow keeps Hangfire Failed-state semantics; AutomaticRetry OnlyOn
            // DdbBusy/DdbBuildInProgress still fails non-transient errors fast.
            writeLine($"Build failed: {Describe(ex)}");
            throw;
        }

        writeLine(skipped ? "Done build (skipped: lock held elsewhere)" : "Done build");
    }

    /// <summary>
    /// Minimum time since a pending entry's last build attempt before it is retried
    /// (same 60 s backoff libddb's buildPending applies).
    /// </summary>
    internal const long PendingRetryBackoffSeconds = 60;

    // Same retry policy as BuildWrapper (intentionally duplicated inline - see there).
    //
    // A pending retry is a build attempt: instead of running ddb.BuildPending() (one opaque
    // job for the whole dataset), this enqueues one indexed BuildWrapper per due pending
    // entry, so a failure lands on that entry's own job and BuildStatusService shows it as
    // "failed". The signature is kept so jobs already stored in Hangfire still deserialize.
    // The org/ds slugs come from the job parameters (set by IndexedJobEnqueuer); when they
    // or the DI services are unavailable (no job context), it falls back to ddb.BuildPending().
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException),
            typeof(PendingBuildCoordinatorContentionException)])]
    public static void BuildPendingWrapper(IDDB ddb, PerformContext context)
    {
        Action<string> writeLine = CreateJobWriteLine(context, ddb.DatasetFolderPath);

        writeLine($"In BuildPendingWrapper('{ddb.DatasetFolderPath}')");

        var target = TryResolvePendingRetryTarget(context, out var reason);
        if (target == null)
        {
            writeLine($"Warning: {reason}; falling back to native build pending");
            Log.Warning("BuildPendingWrapper: job context or services unavailable; falling back to native build pending");
            RunNativeBuildPending(ddb, writeLine);
            return;
        }

        using (target.Scope)
        {
            // Missing MySQL configuration must fail visibly instead of silently
            // taking the native fallback without dataset coordination.
            var mysqlConnectionString = context.Storage is MySqlStorage
                ? (target.Scope.Resolve(typeof(IConfiguration)) as IConfiguration)?
                    .GetConnectionString(MagicStrings.HangfireConnectionName)
                : null;
            using var coordination = DatasetBuildCoordinator.Acquire(target.OrgSlug, target.DsSlug,
                context.Storage, context.Connection, mysqlConnectionString);
            EnqueueDuePendingBuilds(ddb, target.Jobs, target.JobIndex, target.OrgSlug, target.DsSlug,
                UtcNowUnixSeconds, writeLine, context.Storage, context.Connection);
        }
    }

    /// <summary>
    /// Testable core of <see cref="BuildPendingWrapper(IDDB, PerformContext)"/> with its
    /// dependencies passed explicitly (never enqueued as a Hangfire job itself).
    /// </summary>
    internal static void BuildPendingWrapper(IDDB ddb, PerformContext context,
        IBackgroundJobsProcessor jobs, IJobIndexQuery jobIndexQuery, string orgSlug, string dsSlug,
        Func<long> nowUnixSeconds, JobStorage storage = null, string mysqlConnectionString = null)
    {
        Action<string> writeLine = CreateJobWriteLine(context, ddb.DatasetFolderPath);

        writeLine($"In BuildPendingWrapper('{ddb.DatasetFolderPath}')");

        using (DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, storage, mysqlConnectionString: mysqlConnectionString))
            EnqueueDuePendingBuilds(ddb, jobs, jobIndexQuery, orgSlug, dsSlug, nowUnixSeconds, writeLine, storage);
    }

    private static long UtcNowUnixSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed record PendingRetryTarget(
        JobActivatorScope Scope, IBackgroundJobsProcessor Jobs, IJobIndexQuery JobIndex,
        string OrgSlug, string DsSlug);

    // Resolves what the enqueue path needs from the running job. Returns null (with a reason)
    // when anything is missing, so the caller can fall back to the native path.
    private static PendingRetryTarget TryResolvePendingRetryTarget(PerformContext context, out string reason)
    {
        if (context?.BackgroundJob?.Id == null || context.Connection == null)
        {
            reason = "no job context";
            return null;
        }

        string orgSlug, dsSlug;
        try
        {
            // Raw read (as BuildRetrySchedulerFilter does): IndexedJobEnqueuer stores the slugs
            // as plain strings, not JSON, so PerformContext.GetJobParameter<T> would not parse them.
            var jobId = context.BackgroundJob.Id;
            orgSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.OrgSlug);
            dsSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.DsSlug);
        }
        catch (Exception ex)
        {
            reason = $"cannot read job parameters ({Describe(ex)})";
            return null;
        }

        if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
        {
            reason = "job has no organization/dataset parameters";
            return null;
        }

        JobActivatorScope scope = null;
        try
        {
            scope = JobActivator.Current.BeginScope(
                new JobActivatorContext(context.Connection, context.BackgroundJob, context.CancellationToken));
            var jobs = scope.Resolve(typeof(IBackgroundJobsProcessor)) as IBackgroundJobsProcessor;
            var jobIndex = scope.Resolve(typeof(IJobIndexQuery)) as IJobIndexQuery;

            if (jobs == null || jobIndex == null)
            {
                scope.Dispose();
                reason = "job services are not available";
                return null;
            }

            reason = null;
            return new PendingRetryTarget(scope, jobs, jobIndex, orgSlug, dsSlug);
        }
        catch (Exception ex)
        {
            scope?.Dispose();
            reason = $"cannot resolve job services ({Describe(ex)})";
            return null;
        }
    }

    private static void EnqueueDuePendingBuilds(IDDB ddb, IBackgroundJobsProcessor jobs,
        IJobIndexQuery jobIndexQuery, string orgSlug, string dsSlug, Func<long> nowUnixSeconds,
        Action<string> writeLine, JobStorage storage = null, IStorageConnection connection = null)
    {
        writeLine("Enqueueing due pending builds");

        try
        {
            var pending = ddb.GetPendingBuildInfo() ?? [];
            var now = nowUnixSeconds();

            var emptyPath = pending.Count(p => string.IsNullOrWhiteSpace(p.Path));
            var withPath = pending.Where(p => !string.IsNullOrWhiteSpace(p.Path)).ToArray();
            var due = withPath.Where(p => now - p.LastAttempt >= PendingRetryBackoffSeconds).ToArray();
            var inBackoff = withPath.Length - due.Length;

            var activePaths = new System.Collections.Generic.HashSet<string>();
            if (due.Length != 0)
            {
                var duePaths = due.Select(p => p.Path).ToHashSet();
                var indexed = jobIndexQuery.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue)
                    .GetAwaiter().GetResult()
                    .Where(j => j.ToolId == HeavyToolIds.Build && j.Path != null && duePaths.Contains(j.Path));
                using var ownedConnection = connection == null ? (storage ?? JobStorage.Current).GetConnection() : null;
                foreach (var row in indexed)
                {
                    // Hangfire is authoritative: a missing/terminal job or an
                    // interrupted, unqueued Indexing publication cannot suppress
                    // this pending entry forever through a stale Created index row.
                    var actualState = (connection ?? ownedConnection).GetStateData(row.JobId)?.Name;
                    if (actualState != null && TaskStateCatalog.Active.Contains(actualState))
                        activePaths.Add(row.Path);
                }
            }

            var enqueued = 0;
            var active = 0;
            foreach (var entry in due)
            {
                // Add() is false when a job is already active, or was enqueued above for a
                // duplicate path.
                if (!activePaths.Add(entry.Path))
                {
                    active++;
                    continue;
                }

                var path = entry.Path;
                var meta = new IndexPayload(orgSlug, dsSlug, entry.Hash, MagicStrings.AutoBuildServiceUserId,
                    null, path);
                var jobId = jobs.EnqueueIndexed(() => BuildWrapper(ddb, path, false, null), meta);
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException("Pending build enqueue returned no job ID");
                enqueued++;
                writeLine($"Enqueued build of '{path}' (job {jobId})");
            }

            writeLine($"Pending builds: {pending.Length}; enqueued {enqueued}; skipped {inBackoff + active + emptyPath} " +
                      $"({inBackoff} attempted less than {PendingRetryBackoffSeconds}s ago, " +
                      $"{active} with an active build job, {emptyPath} without a path)");
        }
        catch (Exception ex)
        {
            writeLine($"Build pending failed: {Describe(ex)}");
            throw;
        }

        writeLine("Done build pending");
    }

    // Legacy path: libddb runs every due pending build inline in this job.
    private static void RunNativeBuildPending(IDDB ddb, Action<string> writeLine)
    {
        writeLine("Running build pending");
        var skipped = false;
        try
        {
            ddb.BuildPending();
        }
        catch (DdbBuildInProgressException ex)
        {
            // Benign: the lock holder will process the pending builds. See BuildWrapper.
            writeLine($"Build lock currently held by another process ({ex.Message}); skipping");
            skipped = true;
        }
        catch (Exception ex)
        {
            // See BuildWrapper: persist the failure reason into the JobIndex log tail,
            // then rethrow so Hangfire marks the job Failed (and retries apply per policy).
            writeLine($"Build pending failed: {Describe(ex)}");
            throw;
        }

        writeLine(skipped ? "Done build pending (skipped: lock held elsewhere)" : "Done build pending");
    }

    // Same retry policy as BuildWrapper (intentionally duplicated inline - see there).
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public static void CleanupWrapper(IDDB ddb, PerformContext context)
    {
        Action<string> writeLine = CreateJobWriteLine(context, ddb.DatasetFolderPath);

        writeLine($"In CleanupWrapper('{ddb.DatasetFolderPath}')");

        var result = ddb.Cleanup();
        writeLine($"Removed {result.Entries?.Length ?? 0} entries and {result.Builds?.Length ?? 0} build artifacts");
    }

    // Same retry policy as BuildWrapper (intentionally duplicated inline - see there).
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public static void GenerateThumbnailWrapper(IDDB ddb, string path, int size, string dest,
        PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In GenerateThumbnailWrapper('{ddb.DatasetFolderPath}', '{path}', '{size}', '{dest}')");

        writeLine("Running generate thumbnail");
        var result = ddb.GenerateThumbnail(path, size);

        if (result != null)
        {
            writeLine("Saving thumbnail");
            File.WriteAllBytes(dest, result);
        }
        else
        {
            writeLine("Thumbnail generation failed");
        }

        writeLine("Done generate thumbnail");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void SafeDelete(string path, PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In SafeDelete('{path}')");

        if (File.Exists(path))
        {
            var result = FileSystem.SafeDelete(path);
            writeLine(result ? "File deleted successfully" : "Cannot delete file");
        }
        else if (Directory.Exists(path))
        {
            var result = FileSystem.SafeFolderDelete(path);
            writeLine(result ? "Folder deleted successfully" : "Cannot delete folder");
        }
        else
        {
            writeLine("No file or folder found");
        }
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void CleanupExpiredJobs(PerformContext context)
    {
        using var connection = JobStorage.Current.GetConnection();

        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        var toDelete = connection.GetRecurringJobs()
            .Where(j => j.LastJobState == "Failed" && j.CreatedAt < DateTime.Now.AddDays(-30))
            .ToList();

        writeLine($"Found {toDelete.Count} jobs to delete");

        foreach (var job in toDelete)
        {
            writeLine($"Deleting job {job.Id}");
            RecurringJob.RemoveIfExists(job.Id);
        }
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [JobExpiration(ExpirationTimeoutInMinutes = 5)]
    public static void DummyJob(PerformContext context)
    {
        using var connection = JobStorage.Current.GetConnection();

        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;
        writeLine("Dummy job");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [JobExpiration(ExpirationTimeoutInMinutes = 60)]
    public static async Task SyncJobIndexStatesWrapper(PerformContext context, IJobIndexQuery jobIndexQuery, IJobIndexWriter jobIndexWriter)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine("Starting JobIndex state synchronization wrapper");

        try
        {
            var logger = Log.ForContext("SourceContext", "HangfireUtils");
            await SyncJobIndexStatesAsync(jobIndexQuery, jobIndexWriter, logger);
            writeLine("JobIndex state synchronization completed successfully");
        }
        catch (Exception ex)
        {
            writeLine($"Error during JobIndex state synchronization: {ex.Message}");
            throw;
        }
    }

    private static async Task SyncJobIndexStatesAsync(IJobIndexQuery jobIndexQuery, IJobIndexWriter jobIndexWriter, ILogger logger, CancellationToken ct = default)
    {
        logger.Information("Starting JobIndex state synchronization");

        try
        {
            // Get all JobIndex records with "Created" state
            var createdJobs = await jobIndexQuery.GetByStateAsync("Created", ct: ct);
            logger.Information("Found {Count} JobIndex entries with 'Created' state", createdJobs.Length);

            var updatedCount = 0;
            var notFoundCount = 0;

            foreach (var jobIndex in createdJobs)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    // Get job details from Hangfire
                    var monitoringApi = JobStorage.Current.GetMonitoringApi();
                    var jobDetails = monitoringApi.JobDetails(jobIndex.JobId);

                    if (jobDetails?.History != null && jobDetails.History.Count > 0)
                    {
                        var latestHistoryEntry = jobDetails.History[0];
                        var currentState = latestHistoryEntry.StateName;

                        // If the state in Hangfire is different from "Created", update it
                        if (currentState != "Created")
                        {
                            // Use the actual timestamp from Hangfire's state history instead of UtcNow
                            var stateTimestamp = latestHistoryEntry.CreatedAt;

                            logger.Debug("Updating job {JobId} from 'Created' to '{CurrentState}' with timestamp {Timestamp}",
                                jobIndex.JobId, currentState, stateTimestamp);

                            await jobIndexWriter.UpdateStateAsync(jobIndex.JobId, currentState, stateTimestamp, ct);
                            updatedCount++;
                        }
                    }
                    else
                    {
                        // Job not found in Hangfire, might be deleted or expired
                        logger.Debug("Job {JobId} not found in Hangfire, marking as deleted", jobIndex.JobId);

                        // For deleted jobs, we use UtcNow since this is when we discovered they're missing
                        await jobIndexWriter.UpdateStateAsync(jobIndex.JobId, "Deleted", DateTime.UtcNow, ct);
                        notFoundCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Error processing job {JobId}", jobIndex.JobId);
                }
            }

            logger.Information("JobIndex state synchronization completed. Updated: {UpdatedCount}, Not found: {NotFoundCount}", updatedCount, notFoundCount);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error during JobIndex state synchronization");
            throw;
        }
    }

    // Same retry policy as BuildWrapper (intentionally duplicated inline - see there).
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public static void MaskBordersWrapper(IDDB ddb, string inputPath, string outputPath,
        int nearDist, bool white, PerformContext context)
    {
        Action<string> writeLine = CreateJobWriteLine(context, ddb.DatasetFolderPath);

        writeLine($"In MaskBordersWrapper('{ddb.DatasetFolderPath}', '{inputPath}')");

        writeLine("Running mask borders");
        ddb.MaskBorders(inputPath, outputPath, nearDist, white);

        writeLine("Adding masked file to index");
        ddb.AddRaw(outputPath);

        writeLine("Building COG for masked file");
        ddb.Build(outputPath);

        writeLine("Done mask borders");
    }

}