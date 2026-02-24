#nullable enable
using System;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Periodic cleanup service for the JobIndices table.
/// Removes terminal (Succeeded/Failed/Deleted) records older than the configured retention period.
/// </summary>
public class JobIndexCleanupService
{
    private readonly IJobIndexWriter _jobIndexWriter;
    private readonly AppSettings _settings;
    private readonly ILogger<JobIndexCleanupService> _logger;

    public JobIndexCleanupService(
        IJobIndexWriter jobIndexWriter,
        IOptions<AppSettings> settings,
        ILogger<JobIndexCleanupService> logger)
    {
        _jobIndexWriter = jobIndexWriter ?? throw new ArgumentNullException(nameof(jobIndexWriter));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Deletes terminal JobIndex records (Succeeded, Failed, Deleted) older than the configured retention period.
    /// </summary>
    /// <param name="context">Hangfire context for console logging.</param>
    [AutomaticRetry(Attempts = 1)]
    public async Task CleanupOldJobIndicesAsync(PerformContext? context = null)
    {
        void WriteLine(string message)
        {
            _logger.LogInformation(message);
            context?.WriteLine(message);
        }

        var retentionDays = _settings.JobIndexRetentionDays;
        if (retentionDays <= 0)
            retentionDays = 60;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        WriteLine($"Starting JobIndex cleanup: removing terminal records older than {retentionDays} days (before {cutoff:u})");

        try
        {
            var deleted = await _jobIndexWriter.DeleteTerminalBeforeAsync(cutoff);
            WriteLine($"JobIndex cleanup completed: {deleted} records removed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during JobIndex cleanup");
            context?.WriteLine($"Error during JobIndex cleanup: {ex.Message}");
            throw;
        }
    }
}
