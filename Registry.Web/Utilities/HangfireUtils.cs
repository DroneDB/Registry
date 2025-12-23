using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Storage;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Services.Ports;
using Serilog;

namespace Registry.Web.Utilities;

public static class HangfireUtils
{
    private static readonly IFileSystem FileSystem = new FileSystem();

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void BuildWrapper(IDDB ddb, string path, bool force,
        PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In BuildWrapper('{ddb.DatasetFolderPath}', '{path}', '{force}')");

        writeLine("Running build");
        ddb.Build(path, force: force);

        writeLine("Done build");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void BuildPendingWrapper(IDDB ddb, PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In BuildPendingWrapper('{ddb.DatasetFolderPath}')");

        writeLine("Running build pending");
        ddb.BuildPending();

        writeLine("Done build pending");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
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

}