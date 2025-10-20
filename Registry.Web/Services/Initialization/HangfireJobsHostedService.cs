using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// IHostedService wrapper for Hangfire jobs initialization.
/// Can be used independently in ProcessingNode or as part of AppInitializer in WebServer.
/// </summary>
public class HangfireJobsHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<HangfireJobsHostedService> _logger;

    public HangfireJobsHostedService(IServiceProvider services, ILogger<HangfireJobsHostedService> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hangfire jobs initialization starting");

        try
        {
            var initializer = new HangfireJobsInitializer(_services, _logger);

            return initializer.InitializeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Hangfire jobs");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hangfire jobs service stopping");
        return Task.CompletedTask;
    }
}
