using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Orchestrates application initialization tasks during startup.
/// Executes database setup, cache validation, cache configuration, and Hangfire job setup.
/// </summary>
public class AppInitializer : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppInitializer> _logger;
    private readonly CancellationTokenSource _cts;

    public AppInitializer(IServiceScopeFactory scopeFactory, ILogger<AppInitializer> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cts = new CancellationTokenSource();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Application initialization starting");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            await InitializeAsync(linkedCts.Token).ConfigureAwait(false);
            _logger.LogInformation("Application initialization completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Application initialization was canceled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Application initialization failed");
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILogger<AppInitializer>>();

        // Execute initialization steps in order
        await new DatabaseInitializer(services, logger).InitializeAsync(token);
        await new CacheInitializer(services, logger).InitializeAsync(token);
        await new DatasetVisibilityCacheInitializer(services, logger).PreloadAsync(token);

        await new HangfireJobsInitializer(services, logger).InitializeAsync(token);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Application initialization stopping");
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
