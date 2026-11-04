using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Background startup task of a host with the InMemory job storage: the tasks the earlier process left queued
/// or running lost their job with the storage, so they end (<see cref="JobIndexSyncService.CloseLostAsync"/>)
/// instead of showing as running for ever. It runs once the host has started, so a failure is logged by the
/// runner and does not stop the start; the Hangfire servers may run meanwhile, as the sweep never touches a
/// row changed after the process started.
/// </summary>
internal sealed class LostJobsStartupTask : IBackgroundStartupTask
{
    private readonly JobIndexSyncService _sync;

    public LostJobsStartupTask(JobIndexSyncService sync)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
    }

    public Task ExecuteAsync(CancellationToken stoppingToken) =>
        _sync.CloseLostAsync(ProcessStartedUtc(), ct: stoppingToken);

    private static DateTime ProcessStartedUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
