#nullable enable
using System;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Web.Attributes;

namespace Registry.Web.Services.Jobs;

/// <summary>The job the Hangfire health check enqueues to see that a server runs jobs.</summary>
[AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
[JobExpiration(ExpirationTimeoutInMinutes = 5)]
public class HealthCheckJob
{
    private readonly JobStorage _storage;
    private readonly ILogger<HealthCheckJob> _logger;

    public HealthCheckJob(JobStorage storage, ILogger<HealthCheckJob> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Opens a storage connection and writes one line.</summary>
    public void Ping(PerformContext? context)
    {
        using var connection = _storage.GetConnection();
        JobConsole.For(_logger, context).Debug("Health check job");
    }
}
