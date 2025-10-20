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

        SetupRecurringJobs(appSettings);

        _logger.LogInformation("Hangfire jobs initialization completed");

        return Task.CompletedTask;
    }

    private void SetupRecurringJobs(AppSettings appSettings)
    {
        // Cleanup expired jobs
        var cleanupCron = !string.IsNullOrWhiteSpace(appSettings.CleanupExpiredJobsCron)
            ? appSettings.CleanupExpiredJobsCron
            : Cron.Daily();

        RecurringJob.AddOrUpdate(
            "cleanup-expired-jobs",
            () => HangfireUtils.CleanupExpiredJobs(null),
            cleanupCron);

        _logger.LogInformation("Scheduled 'cleanup-expired-jobs' with cron: {Cron}", cleanupCron);

        // Sync JobIndex states every 5 minutes
        var syncCron = string.IsNullOrWhiteSpace(appSettings.SyncJobIndexStatesCron) ? "*/5 * * * *" : appSettings.SyncJobIndexStatesCron;

        RecurringJob.AddOrUpdate<JobIndexSyncService>(
            "sync-jobindex-states",
            service => service.SyncJobIndexStates(null),
            syncCron);

        _logger.LogInformation("Scheduled 'sync-jobindex-states' with cron: {Cron}", syncCron);

        // Process pending builds every minute
        var processCron = string.IsNullOrWhiteSpace(appSettings.ProcessPendingBuildsCron) ? "* * * * *" : appSettings.ProcessPendingBuildsCron;

        RecurringJob.AddOrUpdate<BuildPendingService>(
            "process-pending-builds",
            service => service.ProcessPendingBuilds(null),
            processCron);

        _logger.LogInformation("Scheduled 'process-pending-builds' with cron: {Cron}", processCron);
    }
}
