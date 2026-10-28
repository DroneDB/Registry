using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of the recurring jobs and the job retention.</summary>
public sealed class JobScheduleSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;

    public JobScheduleSections(ConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.CronJobs, BuildCronJobsSection())
    ];

    private ConfigurationSectionDataDto BuildCronJobsSection()
    {
        var jobs = _view.Configured<JobScheduleOptions>();
        var jobsDefault = _view.Shipped<JobScheduleOptions>();
        return Section("CronJobs", "Cron Jobs",
            "Recurring background job schedules (cron expressions) and retention periods.",
            [
                StringField("CleanupExpiredJobsCron", "Cleanup Expired Jobs Cron",
                    "Cron expression for cleaning up expired Hangfire jobs. Default: '0 * * * *' (hourly). Set to 'disabled' to remove.",
                    jobs.CleanupExpiredJobsCron, jobsDefault.CleanupExpiredJobsCron, "cron"),

                StringField("SyncJobIndexStatesCron", "Sync Job Index States Cron",
                    "Cron expression for reconciling JobIndex states across restarts. Default: '0 * * * *' (hourly).",
                    jobs.SyncJobIndexStatesCron, jobsDefault.SyncJobIndexStatesCron, "cron"),

                StringField("RecoverIndexedJobPublicationsCron", "Recover Indexed Job Publications Cron",
                    "Cron expression for completing indexed job publications interrupted before the job became runnable. Default: '* * * * *' (every minute).",
                    jobs.RecoverIndexedJobPublicationsCron, jobsDefault.RecoverIndexedJobPublicationsCron, "cron"),

                StringField("ProcessPendingBuildsCron", "Process Pending Builds Cron",
                    "Cron expression for retrying pending builds. Default: '0 */6 * * *' (every 6 hours).",
                    jobs.ProcessPendingBuildsCron, jobsDefault.ProcessPendingBuildsCron, "cron"),

                StringField("OrphanedDatasetCleanupCron", "Orphaned Dataset Cleanup Cron",
                    "Cron expression for cleaning up orphaned dataset folders. Default: '0 3 * * *' (daily at 3 AM).",
                    jobs.OrphanedDatasetCleanupCron, jobsDefault.OrphanedDatasetCleanupCron, "cron"),

                StringField("DatasetCleanupCron", "Dataset Cleanup Cron",
                    "Cron expression for full DDB cleanup (entries + build artifacts) on all datasets. Default: '0 0 * * *' (daily at midnight).",
                    jobs.DatasetCleanupCron, jobsDefault.DatasetCleanupCron, "cron"),

                StringField("ArtifactCompletenessCheckerCron", "Artifact Completeness Checker Cron",
                    "Cron expression for scanning entries and rebuilding incomplete artifacts. Default: '0 2 * * *' (daily at 2 AM).",
                    jobs.ArtifactCompletenessCheckerCron, jobsDefault.ArtifactCompletenessCheckerCron, "cron"),

                StringField("JobIndexCleanupCron", "Job Index Cleanup Cron",
                    "Cron expression for purging old terminal JobIndex records. Default: '0 4 * * *' (daily at 4 AM).",
                    jobs.JobIndexCleanupCron, jobsDefault.JobIndexCleanupCron, "cron"),

                NumberField("JobIndexRetentionDays", "Job Index Retention (days)",
                    "Number of days to keep terminal (Succeeded/Failed/Deleted) JobIndex records.",
                    jobs.JobIndexRetentionDays, jobsDefault.JobIndexRetentionDays,
                    minValue: 1, unit: "days"),

                NumberField("HangfireJobRetentionDays", "Hangfire Job Retention (days)",
                    "Expiration timeout for succeeded Hangfire jobs. Failed jobs are kept for diagnostics. Minimum: 1 day.",
                    jobs.HangfireJobRetentionDays, jobsDefault.HangfireJobRetentionDays,
                    minValue: 1, unit: "days"),

                NumberField("HangfireInvisibilityTimeoutHours", "Hangfire Invisibility Timeout (hours)",
                    "MySQL storage: hours before a running job is considered abandoned and eligible for re-dequeue. Raise for long-running builds. Minimum: 1 hour.",
                    jobs.HangfireInvisibilityTimeoutHours, jobsDefault.HangfireInvisibilityTimeoutHours,
                    minValue: 1, unit: "hours")
            ]);
    }
}
