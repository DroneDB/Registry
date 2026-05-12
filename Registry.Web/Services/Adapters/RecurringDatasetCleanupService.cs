#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring service that runs DDB cleanup (entries + build artifacts) on every
/// dataset of every organization. Scheduled via Hangfire (cron from
/// <see cref="Registry.Web.Models.Configuration.AppSettings.DatasetCleanupCron"/>).
/// </summary>
public class RecurringDatasetCleanupService
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly ILogger<RecurringDatasetCleanupService> _logger;

    public RecurringDatasetCleanupService(
        RegistryContext context,
        IDdbManager ddbManager,
        ILogger<RecurringDatasetCleanupService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [AutomaticRetry(Attempts = 1)]
    public async Task CleanupAllDatasetsAsync(PerformContext? context = null)
    {
        void WriteLine(string message)
        {
            _logger.LogInformation(message);
            context?.WriteLine(message);
        }

        WriteLine("Starting recurring dataset cleanup across all organizations");

        var datasets = await _context.Datasets
            .AsNoTracking()
            .Include(d => d.Organization)
            .Select(d => new { d.Slug, OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToArrayAsync();

        var totalEntries = 0;
        var totalBuilds = 0;
        var totalSucceeded = 0;
        var totalFailed = 0;

        foreach (var ds in datasets)
        {
            var key = $"{ds.OrgSlug}/{ds.Slug}";
            try
            {
                var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
                var result = ddb.Cleanup();

                var entries = result.Entries?.Length ?? 0;
                var builds = result.Builds?.Length ?? 0;
                totalEntries += entries;
                totalBuilds += builds;
                totalSucceeded++;

                if (entries > 0 || builds > 0)
                    WriteLine($"Cleaned {key}: removed {entries} entries, {builds} build artifacts");
            }
            catch (Exception ex)
            {
                totalFailed++;
                _logger.LogWarning(ex, "Cleanup failed for {Key}", key);
                context?.WriteLine($"WARNING: Cleanup failed for {key}: {ex.Message}");
            }
        }

        WriteLine(
            $"Recurring dataset cleanup completed: {totalSucceeded}/{datasets.Length} datasets " +
            $"processed, {totalFailed} failed, {totalEntries} entries removed, " +
            $"{totalBuilds} build artifacts removed");
    }
}
