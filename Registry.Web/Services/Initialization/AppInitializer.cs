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

        // Execute critical initialization steps in order
        await new DatabaseInitializer(services, logger).InitializeAsync(token);
        await new CacheInitializer(services, logger).InitializeAsync(token);

        await new HangfireJobsInitializer(services, logger).InitializeAsync(token);

        // Dataset visibility cache preload runs in background after startup completes.
        // It opens every .ddb and is non-critical: the cache repopulates lazily on access.
        // Blocking startup on tens of thousands of dataset opens is unacceptable.
        _ = Task.Run(() => RunVisibilityPreloadInBackgroundAsync(_cts.Token), CancellationToken.None);
    }

    private async Task RunVisibilityPreloadInBackgroundAsync(CancellationToken token)
    {
        try
        {
            // Give the host a moment to finish starting before we begin heavy work.
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

            using var scope = _scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var logger = services.GetRequiredService<ILogger<AppInitializer>>();

            await new DatasetVisibilityCacheInitializer(services, logger).PreloadAsync(token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Dataset visibility cache preload canceled (shutdown)");
        }
        catch (Exception ex)
        {
            // Never let a background failure crash the host
            _logger.LogError(ex, "Dataset visibility cache background preload failed");
        }
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
