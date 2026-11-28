using System;
using System.Linq;
using System.Threading;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring job that removes what libddb's cache and its folders in the temporary folder kept past their ages
/// (<see cref="IDdbWrapper.CleanupCache"/>): rendered tiles, downloaded copies of remote rasters, DSM tiles, the folders
/// that crashed or interrupted requests left, and the artefact cache an earlier libddb kept. Nothing else removes them
/// but a tile request now and then, which only a busy map server makes often enough. Scheduled via Hangfire (cron from
/// <see cref="JobScheduleOptions.CacheCleanupCron"/>, ages from the <c>CacheCleanup*Days</c> settings).
/// </summary>
/// <remarks>
/// One run at a time across the hosts: two runs would walk the same folders. libddb's cache is the one of the
/// process that runs the job, so a host with a cache of its own has it cleaned when it runs the job, and by its tile
/// requests in between.
/// </remarks>
[AutomaticRetry(Attempts = 1)]
[OneRunAtATime]
public class RecurringCacheCleanupService
{
    private const double BytesPerMiB = 1024d * 1024;

    private readonly IDdbWrapper _ddb;
    private readonly JobScheduleOptions _schedule;
    private readonly ILogger<RecurringCacheCleanupService> _logger;

    public RecurringCacheCleanupService(IDdbWrapper ddb, IOptions<JobScheduleOptions> schedule,
        ILogger<RecurringCacheCleanupService> logger)
    {
        _ddb = ddb ?? throw new ArgumentNullException(nameof(ddb));
        _schedule = schedule?.Value ?? throw new ArgumentNullException(nameof(schedule));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Removes the cache entries older than the configured ages and logs what each kind freed.
    /// </summary>
    /// <param name="context">Hangfire context for console logging.</param>
    /// <param name="cancellationToken">Cancelled when the server shuts down: libddb stops at its next entry.</param>
    public void CleanupCache(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        var options = OptionsOf(_schedule);
        console.Info("Starting the cleanup of the libddb cache: tiles older than {TileDays} days, downloads older than " +
                     "{DownloadDays} days, DSM tiles older than {DsmDays} days, temporary folders older than {TempDays} days",
            options.TileDays, options.DownloadDays, options.DsmDays, options.TempDays);

        var result = _ddb.CleanupCache(options, cancellationToken);

        foreach (var (kind, count) in result.Kinds.Where(k => k.Value.Entries > 0).OrderBy(k => k.Key, StringComparer.Ordinal))
            console.Info("Removed {Entries} {Kind} entries ({MiB:0.0} MiB)", count.Entries, kind, ToMiB(count.Bytes));

        console.Info("Cache cleanup completed: {Entries} entries removed, {MiB:0.0} MiB freed",
            result.TotalEntries, ToMiB(result.TotalBytes));
    }

    /// <summary>The ages of the settings, every one sent so the job does what the settings page shows.</summary>
    internal static CacheCleanupOptions OptionsOf(JobScheduleOptions schedule) => new(
        TileDays: schedule.CacheCleanupTileDays,
        DownloadDays: schedule.CacheCleanupDownloadDays,
        DsmDays: schedule.CacheCleanupDsmDays,
        TempDays: schedule.CacheCleanupTempDays);

    private static double ToMiB(long bytes) => bytes / BytesPerMiB;
}
