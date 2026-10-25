using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Registry.Web.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of every host with the job storage (<see cref="StartupTaskOrder.RecurringJobs"/>): defines
/// the recurring jobs before the Hangfire servers start.
/// </summary>
internal sealed class HangfireJobsInitializer : IStartupTask
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly JobScheduleOptions _schedule;
    private readonly UpdateCheckOptions _updateCheck;
    private readonly IInstanceIdentity _identity;
    private readonly ILogger<HangfireJobsInitializer> _logger;

    /// <summary>
    /// Recurring job ids retired in newer versions. Removed on startup so older
    /// deployments stop executing obsolete definitions left in Hangfire storage.
    /// </summary>
    private static readonly string[] DeprecatedRecurringJobIds = [];

    public HangfireJobsInitializer(IRecurringJobManager recurringJobManager, IOptions<JobScheduleOptions> schedule,
        IOptions<UpdateCheckOptions> updateCheck, IInstanceIdentity identity, ILogger<HangfireJobsInitializer> logger)
    {
        _recurringJobManager = recurringJobManager ?? throw new ArgumentNullException(nameof(recurringJobManager));
        _schedule = (schedule ?? throw new ArgumentNullException(nameof(schedule))).Value;
        _updateCheck = (updateCheck ?? throw new ArgumentNullException(nameof(updateCheck))).Value;
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
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
            CronSetting.Resolve(appSettings.CleanupExpiredJobsCron, Cron.Daily()),
            cron => recurringJobManager.AddOrUpdate<ExpiredRecurringJobsSweep>(
                RecurringJobIds.CleanupExpiredJobs,
                sweep => sweep.Run(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.SyncJobIndexStates,
            CronSetting.Resolve(appSettings.SyncJobIndexStatesCron, "0 * * * *"),
            cron => recurringJobManager.AddOrUpdate<JobIndexSyncService>(
                RecurringJobIds.SyncJobIndexStates,
                service => service.SyncJobIndexStates(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.RecoverIndexedJobPublications,
            CronSetting.Resolve(appSettings.RecoverIndexedJobPublicationsCron, "* * * * *"),
            cron => recurringJobManager.AddOrUpdate<IndexedJobPublicationRecoveryService>(
                RecurringJobIds.RecoverIndexedJobPublications,
                service => service.RecoverPublications(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.ProcessPendingBuilds,
            CronSetting.Resolve(appSettings.ProcessPendingBuildsCron, "0 */6 * * *"),
            cron => recurringJobManager.AddOrUpdate<BuildPendingService>(
                RecurringJobIds.ProcessPendingBuilds,
                service => service.ProcessPendingBuilds(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.CleanupOrphanedDatasets,
            CronSetting.Resolve(appSettings.OrphanedDatasetCleanupCron, "0 3 * * *"),
            cron => recurringJobManager.AddOrUpdate<OrphanedDatasetCleanupService>(
                RecurringJobIds.CleanupOrphanedDatasets,
                service => service.CleanupOrphanedFoldersAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.CleanupOldJobIndices,
            CronSetting.Resolve(appSettings.JobIndexCleanupCron, "0 4 * * *"),
            cron => recurringJobManager.AddOrUpdate<JobIndexCleanupService>(
                RecurringJobIds.CleanupOldJobIndices,
                service => service.CleanupOldJobIndicesAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.RecurringDatasetCleanup,
            CronSetting.Resolve(appSettings.DatasetCleanupCron, "0 0 * * *"),
            cron => recurringJobManager.AddOrUpdate<RecurringDatasetCleanupService>(
                RecurringJobIds.RecurringDatasetCleanup,
                service => service.CleanupAllDatasetsAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.CheckArtifactCompleteness,
            CronSetting.Resolve(appSettings.ArtifactCompletenessCheckerCron, "0 2 * * *"),
            cron => recurringJobManager.AddOrUpdate<ArtifactCompletenessCheckerService>(
                RecurringJobIds.CheckArtifactCompleteness,
                service => service.CheckAndQueueAsync(null, CancellationToken.None),
                cron));

        ScheduleJob(recurringJobManager, RecurringJobIds.IndexReconciliation,
            CronSetting.Resolve(appSettings.IndexReconciliationCron, "0 * * * *"),
            cron => recurringJobManager.AddOrUpdate<IndexReconciliationService>(
                RecurringJobIds.IndexReconciliation,
                service => service.ReconcileAllDatasetsAsync(null, CancellationToken.None),
                cron));

        // The identity exists before the jobs are defined (StartupTaskOrder.InstanceIdentity)
        ScheduleJob(recurringJobManager, RecurringJobIds.CheckUpdates,
            CronSetting.Resolve(_updateCheck.UpdateCheckCron, InstanceDailyCron(_identity.Current.Id)),
            cron => recurringJobManager.AddOrUpdate<IUpdateCheckService>(
                RecurringJobIds.CheckUpdates,
                service => service.CheckForUpdatesAsync(CancellationToken.None),
                cron));
    }

    /// <summary>
    /// Once a day at a time derived from the instance id, so the deployments do not all ask in the same minute: the
    /// same id always gives the same time, and the ids are spread evenly over the 24 hours.
    /// </summary>
    internal static string InstanceDailyCron(string instanceId)
    {
        var minuteOfDay = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId.ToLowerInvariant()))) % (24 * 60);
        return $"{minuteOfDay % 60} {minuteOfDay / 60} * * *";
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
}
