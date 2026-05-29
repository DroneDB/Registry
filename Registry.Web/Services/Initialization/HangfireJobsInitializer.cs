using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Handles Hangfire recurring job initialization.
/// </summary>
internal class HangfireJobsInitializer
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public HangfireJobsInitializer(IServiceProvider services, ILogger logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task InitializeAsync(CancellationToken token)
    {
        _logger.LogInformation("Hangfire jobs initialization starting");

        var appSettings = _services.GetRequiredService<IOptions<AppSettings>>().Value;
        var recurringJobManager = _services.GetRequiredService<IRecurringJobManager>();

        SetupRecurringJobs(appSettings, recurringJobManager);

        _logger.LogInformation("Hangfire jobs initialization completed");

        return Task.CompletedTask;
    }

    private void SetupRecurringJobs(AppSettings appSettings, IRecurringJobManager recurringJobManager)
    {
        ScheduleJob(recurringJobManager, "cleanup-expired-jobs",
            ResolveCron(appSettings.CleanupExpiredJobsCron, Cron.Daily()),
            cron => recurringJobManager.AddOrUpdate(
                "cleanup-expired-jobs",
                () => HangfireUtils.CleanupExpiredJobs(null),
                cron));

        ScheduleJob(recurringJobManager, "sync-jobindex-states",
            ResolveCron(appSettings.SyncJobIndexStatesCron, "*/5 * * * *"),
            cron => recurringJobManager.AddOrUpdate<JobIndexSyncService>(
                "sync-jobindex-states",
                service => service.SyncJobIndexStates(null),
                cron));

        ScheduleJob(recurringJobManager, "process-pending-builds",
            ResolveCron(appSettings.ProcessPendingBuildsCron, "* * * * *"),
            cron => recurringJobManager.AddOrUpdate<BuildPendingService>(
                "process-pending-builds",
                service => service.ProcessPendingBuilds(null),
                cron));

        ScheduleJob(recurringJobManager, "cleanup-orphaned-datasets",
            ResolveCron(appSettings.OrphanedDatasetCleanupCron, "0 3 * * *"),
            cron => recurringJobManager.AddOrUpdate<OrphanedDatasetCleanupService>(
                "cleanup-orphaned-datasets",
                service => service.CleanupOrphanedFoldersAsync(null),
                cron));

        ScheduleJob(recurringJobManager, "cleanup-old-jobindices",
            ResolveCron(appSettings.JobIndexCleanupCron, "0 4 * * *"),
            cron => recurringJobManager.AddOrUpdate<JobIndexCleanupService>(
                "cleanup-old-jobindices",
                service => service.CleanupOldJobIndicesAsync(null),
                cron));

        ScheduleJob(recurringJobManager, "recurring-dataset-cleanup",
            ResolveCron(appSettings.DatasetCleanupCron, "0 0 * * *"),
            cron => recurringJobManager.AddOrUpdate<RecurringDatasetCleanupService>(
                "recurring-dataset-cleanup",
                service => service.CleanupAllDatasetsAsync(null),
                cron));

        ScheduleJob(recurringJobManager, "check-artifact-completeness",
            ResolveCron(appSettings.ArtifactCompletenessCheckerCron, "0 2 * * *"),
            cron => recurringJobManager.AddOrUpdate<ArtifactCompletenessCheckerService>(
                "check-artifact-completeness",
                service => service.CheckAndQueueAsync(null),
                cron));
    }

    /// <summary>
    /// Registers or removes a recurring Hangfire job based on the resolved cron expression.
    /// A <see langword="null"/> resolved cron means the job is explicitly disabled.
    /// </summary>
    private void ScheduleJob(IRecurringJobManager manager, string jobId, string resolvedCron,
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
    private static string ResolveCron(string raw, string defaultCron)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultCron;

        return raw.Trim().ToLowerInvariant() is "disabled" or "off" or "none"
            ? null
            : raw;
    }
}
