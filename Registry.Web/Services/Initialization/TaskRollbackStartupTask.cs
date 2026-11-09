using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Hosting;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of every host that runs tasks (<see cref="StartupTaskOrder.TaskRollbacks"/>): before the Hangfire servers
/// start, it finishes the rollback of every ended task whose write record still needs it
/// (<see cref="ITaskRollback.SweepAsync"/>, FR-XC-27): a task cancelled before it started, a rollback that a stop
/// interrupted, a worker that died during it. A sweep that fails is logged: the host still starts, and the next start
/// sweeps again.
/// </summary>
internal sealed class TaskRollbackStartupTask : IStartupTask
{
    private readonly ITaskRollback _rollback;
    private readonly ILogger<TaskRollbackStartupTask> _log;

    public TaskRollbackStartupTask(ITaskRollback rollback,
        ILogger<TaskRollbackStartupTask> log)
    {
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public int Order => StartupTaskOrder.TaskRollbacks;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _rollback.SweepAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "The rollback sweep of the ended tasks failed: it runs again at the next start");
        }
    }
}
