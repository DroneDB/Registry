#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Owns the libddb process lifecycle in every host. Registered as
/// the first hosted service (<see cref="DdbRuntimeServiceCollectionExtensions.AddDdbRuntime{TRuntime}"/>):
/// <list type="bullet">
/// <item><see cref="StartingAsync"/>, before any hosted service starts: checks the ABI, sends the log
/// records to the logger, applies the host budget (<see cref="HostResourceBudget"/>: the per-build
/// environment of a job host, then <c>DDBConfigure</c> with the explicit <see cref="DdbRuntimeOptions"/>
/// or the values derived from the budget), then initializes libddb (<c>DDBRegisterProcess</c>,
/// checked) - the order libddb requires;</item>
/// <item><see cref="StoppedAsync"/>, after every hosted service stopped (the Hangfire servers drained,
/// the web server closed): <c>DDBShutdown</c> checkpoints and closes the index connections and
/// flushes the log, waiting at most <see cref="ShutdownTimeouts.Ddb"/> for calls still running.</item>
/// </list>
/// </summary>
public sealed class DdbRuntimeService : IHostedLifecycleService
{
    private readonly IDdbRuntime _runtime;
    private readonly HostResourceBudget _budget;
    private readonly DdbLogSink _logSink;
    private readonly ILogger<DdbRuntimeService> _logger;

    public DdbRuntimeService(IDdbRuntime runtime, HostResourceBudget budget, DdbLogSink logSink,
        ILogger<DdbRuntimeService> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var capabilities = _runtime.Capabilities;
        _logger.LogInformation("DroneDB: {Capabilities}", capabilities);

        // Before DDBRegisterProcess: the callback's level becomes the process log level, so DEBUG and
        // TRACE records reach the logger without a verbose stdout
        _runtime.SetLogSink(_logSink, _logSink.MinimumLevel);

        _logger.LogInformation("Host budget: {HostBudget}", _budget);
        foreach (var (name, value) in _budget.NativeBuildEnvironment)
        {
            // A value set in the deployment environment wins over the budget
            var effective = _runtime.SetEnvironmentDefault(name, value);
            if (effective != value)
                _logger.LogInformation("Keeping {Name}={Value} from the environment", name, effective);
        }

        _runtime.Configure(_budget.Ddb);
        _runtime.RegisterProcess();

        _logger.LogInformation("DroneDB initialized, configuration {DdbEffectiveConfig}", _runtime.GetEffectiveConfig());
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_runtime.Shutdown(ShutdownTimeouts.Ddb))
                _logger.LogInformation("DroneDB shut down");
            else
                _logger.LogWarning(
                    "DroneDB calls were still running after {Timeout}: new calls are refused, the idle index " +
                    "connections are checkpointed at exit", ShutdownTimeouts.Ddb);
        }
        catch (DdbException ex)
        {
            // A teardown step failed; the library is shut down all the same
            _logger.LogError(ex, "DroneDB shutdown failed");
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
