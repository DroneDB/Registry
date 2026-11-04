using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of a host with the InMemory job storage (<see cref="StartupTaskOrder.LostJobs"/>): the tasks the
/// earlier process left queued or running lost their job with the storage, so they end
/// (<see cref="JobIndexSyncService.CloseLostAsync"/>) instead of showing as running for ever. A failure is
/// logged and does not stop the start.
/// </summary>
internal sealed class LostJobsStartupTask : IStartupTask
{
    private readonly JobIndexSyncService _sync;
    private readonly ILogger<LostJobsStartupTask> _logger;

    public LostJobsStartupTask(JobIndexSyncService sync, ILogger<LostJobsStartupTask> logger)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public int Order => StartupTaskOrder.LostJobs;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sync.CloseLostAsync(ProcessStartedUtc(), ct: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cannot end the tasks lost when the job storage restarted: {Error}", ex.Message);
        }
    }

    private static DateTime ProcessStartedUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
