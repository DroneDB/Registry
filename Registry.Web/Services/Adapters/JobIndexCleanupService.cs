using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Periodic cleanup service for the JobIndices table.
/// Removes terminal (Succeeded/Failed/Deleted) records older than the configured retention period, then takes care of
/// the build notification outbox (<see cref="BuildNotificationCleanup"/>), the other history of the builds.
/// </summary>
/// <remarks>
/// One run at a time across the hosts: two would run the same DELETE (harmless but wasted on a
/// large table). Timeout 0: the run in progress does the work.
/// </remarks>
[AutomaticRetry(Attempts = 1)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class JobIndexCleanupService
{
    private readonly IJobIndexWriter _jobIndexWriter;
    private readonly BuildNotificationCleanup _notifications;
    private readonly JobScheduleOptions _settings;
    private readonly ILogger<JobIndexCleanupService> _logger;

    private const int DefaultRetentionDays = 60;

    public JobIndexCleanupService(
        IJobIndexWriter jobIndexWriter,
        BuildNotificationCleanup notifications,
        IOptions<JobScheduleOptions> settings,
        ILogger<JobIndexCleanupService> logger)
    {
        _jobIndexWriter = jobIndexWriter ?? throw new ArgumentNullException(nameof(jobIndexWriter));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The job as stored by earlier releases: runs with the context's shutdown token.</summary>
    public Task CleanupOldJobIndicesAsync(PerformContext? context) =>
        CleanupOldJobIndicesAsync(context, JobCancellation.Of(context));

    /// <summary>
    /// Deletes terminal JobIndex records (Succeeded, Failed, Deleted) older than the configured retention period.
    /// </summary>
    /// <param name="context">Hangfire context for console logging.</param>
    /// <param name="cancellationToken">Cancelled when the server shuts down.</param>
    public async Task CleanupOldJobIndicesAsync(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);

        var retentionDays = _settings.JobIndexRetentionDays;
        if (retentionDays <= 0)
            retentionDays = DefaultRetentionDays;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        console.Info("Starting JobIndex cleanup: removing terminal records older than {RetentionDays} days (before {Cutoff:u})",
            retentionDays, cutoff);

        try
        {
            var deleted = await _jobIndexWriter.DeleteTerminalBeforeAsync(cutoff, cancellationToken);
            console.Info("JobIndex cleanup completed: {Deleted} records removed", deleted);

            await _notifications.RunAsync(console, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Error(ex, "Error during JobIndex cleanup: {Error}", ex.Message);
            throw;
        }
    }
}
