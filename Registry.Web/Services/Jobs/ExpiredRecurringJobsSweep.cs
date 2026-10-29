using System;
using System.Linq;
using System.Threading;
using Hangfire;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Initialization;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The recurring cleanup of abandoned recurring jobs: removes the ones whose last run failed and
/// that were created more than <see cref="ExpiredAge"/> ago. The application's own recurring jobs
/// (<see cref="RecurringJobIds.Owned"/>) are never removed: they are re-created only at the next
/// start, so one failed run would otherwise disable them until then.
/// </summary>
/// <remarks>
/// No <see cref="DisableConcurrentExecutionAttribute"/>: a daily, short, idempotent run
/// (<c>RemoveIfExists</c>).
/// </remarks>
[AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class ExpiredRecurringJobsSweep
{
    /// <summary>Age after which a recurring job whose last run failed is considered abandoned.</summary>
    internal static readonly TimeSpan ExpiredAge = TimeSpan.FromDays(30);

    private readonly JobStorage _storage;
    private readonly TimeProvider _time;
    private readonly ILogger<ExpiredRecurringJobsSweep> _logger;

    public ExpiredRecurringJobsSweep(JobStorage storage, TimeProvider time, ILogger<ExpiredRecurringJobsSweep> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <returns>How many recurring jobs were removed.</returns>
    public int Run(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        using var connection = _storage.GetConnection();

        // RecurringJobDto.CreatedAt is UTC
        var cutoffUtc = _time.GetUtcNow().UtcDateTime - ExpiredAge;

        var toDelete = connection.GetRecurringJobs()
            .Where(j => j.LastJobState == FailedState.StateName
                        && j.CreatedAt < cutoffUtc
                        && !RecurringJobIds.Owned.Contains(j.Id))
            .ToList();

        console.Info("Found {Count} jobs to delete", toDelete.Count);

        var manager = new RecurringJobManager(_storage);
        foreach (var job in toDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            console.Info("Deleting job {JobId}", job.Id);
            manager.RemoveIfExists(job.Id);
        }

        return toDelete.Count;
    }
}
