using System;
using System.Collections.Concurrent;
using System.Threading;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Lets one run of a job method of a <see cref="OneRunAtATimeAttribute"/> class perform at a time. A run that finds
/// another one performing ends at once without running its body, which Hangfire records as Deleted (its state for
/// a run a filter cancels; the run is not a failure and is not retried): the run in progress does the work, so the
/// skipped one lost nothing.
/// <para>
/// With the MySQL storage the lock is a session lock (<see cref="MySqlSessionLock"/>) taken when the run starts and
/// held until it ends, on every host sharing the database; the server releases it when the host dies. Hangfire's own
/// <c>DisableConcurrentExecution</c> cannot do this with a zero timeout: the storage takes its lock row only when no
/// row of the resource is younger than the timeout, so with 0 any run takes it, and it never expires a lock that is
/// still held. With the in-memory storage, which has one process, it is a semaphore per job method.
/// </para>
/// </summary>
public sealed class OneRunAtATimeFilter(IConfiguration configuration, ILogger<OneRunAtATimeFilter> log) : IServerFilter
{
    private const string ItemKey = "OneRunAtATimeLease";

    /// <summary>Prefix of the MySQL session lock names (64 characters at most with the hash).</summary>
    private const string KeyPrefix = "job-run";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcess = new();

    public void OnPerforming(PerformingContext context)
    {
        var job = context.BackgroundJob.Job;
        if (job?.Type is null || !Attribute.IsDefined(job.Type, typeof(OneRunAtATimeAttribute), true))
            return;

        var resource = $"{job.Type.FullName}.{job.Method.Name}";
        var lease = TryAcquire(context, resource);
        if (lease is null)
        {
            log.LogInformation("Job {JobId} ({Resource}) did not run: another run is in progress", context.BackgroundJob.Id, resource);
            context.Canceled = true;
            return;
        }

        context.Items[ItemKey] = lease;
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var lease) && lease is IDisposable disposable)
            disposable.Dispose();
    }

    private IDisposable? TryAcquire(PerformingContext context, string resource)
    {
        if (context.Storage is not Hangfire.MySql.MySqlStorage)
        {
            var semaphore = InProcess.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
            return semaphore.Wait(0) ? new SemaphoreRelease(semaphore) : null;
        }

        var connectionString = HangfireMySql.ConnectionString(context.Storage, configuration)
            ?? throw new InvalidOperationException("One run at a time requires the configured Hangfire MySQL connection string");
        return MySqlSessionLock.TryAcquire(connectionString, resource, KeyPrefix, TimeSpan.Zero, log);
    }

    private sealed class SemaphoreRelease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
