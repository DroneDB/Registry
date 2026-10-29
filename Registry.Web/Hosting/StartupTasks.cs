using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Hosting;

/// <summary>
/// Work that must finish before the host starts serving: run by <see cref="StartupTaskRunner"/>
/// in <see cref="Order"/>, before any hosted service starts. Dependencies come through the constructor
/// (the runner resolves the tasks from one scope); a task that throws stops the start.
/// </summary>
public interface IStartupTask
{
    /// <summary>Position in the run: lower first (<see cref="StartupTaskOrder"/>).</summary>
    int Order { get; }

    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Non-critical work started once the host has started (every hosted service started, the server
/// listening), on the host's stopping token. A failure is logged and never stops the host.
/// </summary>
public interface IBackgroundStartupTask
{
    Task ExecuteAsync(CancellationToken stoppingToken);
}

/// <summary>The order of the startup tasks: one table instead of registration order.</summary>
public static class StartupTaskOrder
{
    /// <summary>Thread pool minimum (web).</summary>
    public const int ThreadPool = 0;

    /// <summary>Migrations, initial data and the default admin (web; the nodes never migrate).</summary>
    public const int Database = 100;

    /// <summary>Cache connection check and cache providers (every job or web host).</summary>
    public const int Caches = 200;

    /// <summary>
    /// The instance identity (every host with the job storage): created or loaded before the recurring job
    /// definitions, which derive the time of the daily update check from it.
    /// </summary>
    public const int InstanceIdentity = 250;

    /// <summary>Recurring job definitions (every host with the job storage), before the Hangfire servers start.</summary>
    public const int RecurringJobs = 300;
}

/// <summary>
/// Runs the startup tasks of every host. It is an <see cref="IHostedLifecycleService"/>: the host
/// completes <see cref="StartingAsync"/> of every lifecycle service, in registration order, before it
/// calls any <see cref="IHostedService.StartAsync"/>. Registered right after the libddb runtime
/// (initialized in its own <c>StartingAsync</c>), so the tasks run after libddb is ready and before the
/// Hangfire servers, the queues and the web server start, whatever their registration order.
/// </summary>
public sealed class StartupTaskRunner : IHostedLifecycleService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupTaskRunner> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task _background = Task.CompletedTask;

    public StartupTaskRunner(IServiceScopeFactory scopeFactory, ILogger<StartupTaskRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        foreach (var task in scope.ServiceProvider.GetServices<IStartupTask>().OrderBy(t => t.Order))
        {
            var name = task.GetType().Name;
            _logger.LogInformation("Startup task {Task} starting", name);
            try
            {
                await task.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogCritical(ex, "Startup task {Task} failed: the host does not start", name);
                throw;
            }

            _logger.LogInformation("Startup task {Task} completed", name);
        }
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _background = RunBackgroundAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        // The background tasks observe the token; the host's stop budget bounds the wait
        await _background.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _stopping.Dispose();

    private async Task RunBackgroundAsync(CancellationToken stoppingToken)
    {
        // Off the host's start path: StartedAsync returns at once
        await Task.Yield();

        await using var scope = _scopeFactory.CreateAsyncScope();
        foreach (var task in scope.ServiceProvider.GetServices<IBackgroundStartupTask>())
        {
            var name = task.GetType().Name;
            try
            {
                await task.ExecuteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Background startup task {Task} canceled (shutdown)", name);
                return;
            }
            catch (Exception ex)
            {
                // Never let a background failure crash the host
                _logger.LogError(ex, "Background startup task {Task} failed", name);
            }
        }
    }
}

/// <summary>Registration of the startup runner and tasks.</summary>
public static class StartupTaskServiceCollectionExtensions
{
    /// <summary>The runner (once per host; call it right after the libddb runtime).</summary>
    public static IServiceCollection AddStartupTaskRunner(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(StartupTaskRunner)))
            return services;

        return services.AddHostedService<StartupTaskRunner>();
    }

    /// <summary>Adds <typeparamref name="TTask"/> (scoped) once.</summary>
    public static IServiceCollection AddStartupTask<TTask>(this IServiceCollection services)
        where TTask : class, IStartupTask
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStartupTask, TTask>());
        return services;
    }

    /// <summary>Adds <typeparamref name="TTask"/> (scoped) once.</summary>
    public static IServiceCollection AddBackgroundStartupTask<TTask>(this IServiceCollection services)
        where TTask : class, IBackgroundStartupTask
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IBackgroundStartupTask, TTask>());
        return services;
    }

}
