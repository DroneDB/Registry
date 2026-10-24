using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Registry.Web.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of every host with the job storage (<see cref="StartupTaskOrder.RecurringJobs"/>): defines
/// the recurring jobs before the Hangfire servers start.
/// </summary>
internal sealed class HangfireJobsInitializer : IStartupTask
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly JobScheduleOptions _schedule;
    private readonly ILogger<HangfireJobsInitializer> _logger;

    /// <summary>
    /// Recurring job ids retired in newer versions. Removed on startup so older
    /// deployments stop executing obsolete definitions left in Hangfire storage.
    /// </summary>
    private static readonly string[] DeprecatedRecurringJobIds = [];

    public HangfireJobsInitializer(IRecurringJobManager recurringJobManager, IOptions<JobScheduleOptions> schedule,
        ILogger<HangfireJobsInitializer> logger)
    {
        _recurringJobManager = recurringJobManager ?? throw new ArgumentNullException(nameof(recurringJobManager));
        _schedule = (schedule ?? throw new ArgumentNullException(nameof(schedule))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public int Order => StartupTaskOrder.RecurringJobs;

    public Task ExecuteAsync(CancellationToken token)
    {
        _logger.LogInformation("Hangfire jobs initialization starting");

        SetupRecurringJobs(_schedule, _recurringJobManager);

        _logger.LogInformation("Hangfire jobs initialization completed");

        return Task.CompletedTask;
    }

    private void SetupRecurringJobs(JobScheduleOptions appSettings, IRecurringJobManager recurringJobManager)
    {
        // Migration: remove recurring jobs retired in newer versions. Surviving jobs
        // are re-scheduled by the AddOrUpdate calls below, which overwrite any cron
        // previously persisted in Hangfire storage - this is how the legacy per-minute
        // "process-pending-builds" schedule migrates to the new low-frequency cadence.
        foreach (var jobId in DeprecatedRecurringJobIds)
        {
            recurringJobManager.RemoveIfExists(jobId);
            _logger.LogInformation("Removed deprecated recurring job '{JobId}'", jobId);
        }

        ScheduleJob(recurringJobManager, RecurringJobIds.CleanupExpiredJobs,
            ResolveCron(appSettings.CleanupExpiredJobsCron, Cron.Daily()),
            cron => recurringJobManager.AddOrUpdate<ExpiredRecurringJobsSweep>(
                RecurringJobIds.CleanupExpiredJobs,
                sweep => sweep.Run(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.SyncJobIndexStates,
            ResolveCron(appSettings.SyncJobIndexStatesCron, "0 * * * *"),
            cron => recurringJobManager.AddOrUpdate<JobIndexSyncService>(
                RecurringJobIds.SyncJobIndexStates,
                service => service.SyncJobIndexStates(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.RecoverIndexedJobPublications,
            ResolveCron(appSettings.RecoverIndexedJobPublicationsCron, "* * * * *"),
            cron => recurringJobManager.AddOrUpdate<IndexedJobPublicationRecoveryService>(
                RecurringJobIds.RecoverIndexedJobPublications,
                service => service.RecoverPublications(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.ProcessPendingBuilds,
            ResolveCron(appSettings.ProcessPendingBuildsCron, "0 */6 * * *"),
            cron => recurringJobManager.AddOrUpdate<BuildPendingService>(
                RecurringJobIds.ProcessPendingBuilds,
                service => service.ProcessPendingBuilds(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.CleanupOrphanedDatasets,
            ResolveCron(appSettings.OrphanedDatasetCleanupCron, "0 3 * * *"),
            cron => recurringJobManager.AddOrUpdate<OrphanedDatasetCleanupService>(
                RecurringJobIds.CleanupOrphanedDatasets,
                service => service.CleanupOrphanedFoldersAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.CleanupOldJobIndices,
            ResolveCron(appSettings.JobIndexCleanupCron, "0 4 * * *"),
            cron => recurringJobManager.AddOrUpdate<JobIndexCleanupService>(
                RecurringJobIds.CleanupOldJobIndices,
                service => service.CleanupOldJobIndicesAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.RecurringDatasetCleanup,
            ResolveCron(appSettings.DatasetCleanupCron, "0 0 * * *"),
            cron => recurringJobManager.AddOrUpdate<RecurringDatasetCleanupService>(
                RecurringJobIds.RecurringDatasetCleanup,
                service => service.CleanupAllDatasetsAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.CheckArtifactCompleteness,
            ResolveCron(appSettings.ArtifactCompletenessCheckerCron, "0 2 * * *"),
            cron => recurringJobManager.AddOrUpdate<ArtifactCompletenessCheckerService>(
                RecurringJobIds.CheckArtifactCompleteness,
                service => service.CheckAndQueueAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.IndexReconciliation,
            ResolveCron(appSettings.IndexReconciliationCron, "0 * * * *"),
            cron => recurringJobManager.AddOrUpdate<IndexReconciliationService>(
                RecurringJobIds.IndexReconciliation,
                service => service.ReconcileAllDatasetsAsync(null, CancellationToken.None),
                cron));
    }

    /// <summary>
    /// Registers or removes a recurring Hangfire job based on the resolved cron expression.
    /// A <see langword="null"/> resolved cron means the job is explicitly disabled.
    /// </summary>
    private void ScheduleJob(IRecurringJobManager manager, string jobId, string? resolvedCron,
        Action<string> register)
    {
        if (resolvedCron is null)
        {
            manager.RemoveIfExists(jobId);
            _logger.LogInformation("Recurring '{JobId}' is disabled (cron set to 'disabled')", jobId);
        }
        else
        {
            register(resolvedCron);
            _logger.LogInformation("Scheduled '{JobId}' with cron: {Cron}", jobId, resolvedCron);
        }
    }

    /// <summary>
    /// Resolves a raw cron string from configuration:
    /// <list type="bullet">
    ///   <item><description>null or empty/whitespace → <paramref name="defaultCron"/> (use built-in default)</description></item>
    ///   <item><description>"disabled", "off", or "none" (case-insensitive) → <see langword="null"/> (job will be removed)</description></item>
    ///   <item><description>any other value → returned as-is (treated as a valid cron expression)</description></item>
    /// </list>
    /// </summary>
    private static string? ResolveCron(string? raw, string defaultCron)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultCron;

        var trimmed = raw.Trim();
        return trimmed.ToLowerInvariant() is "disabled" or "off" or "none"
            ? null
            : trimmed;
    }
}
