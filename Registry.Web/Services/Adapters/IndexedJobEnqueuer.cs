#nullable enable
using System;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Parameter names used to pass metadata through Hangfire job arguments.
/// </summary>
public static class JobParamKeys
{
    public const string OrgSlug = "orgSlug";
    public const string DsSlug = "dsSlug";
    public const string Path = "path";
    public const string Hash = "hash";
    public const string UserId = "userId";
    public const string Queue = "queue"; // Optional
}

/// <summary>
/// Publishes metadata and JobIndex before making an indexed Hangfire job runnable.
/// </summary>
public class IndexedJobEnqueuer(IBackgroundJobClient client, IServiceProvider sp, ILogger<IndexedJobEnqueuer> log,
    JobStorage? storage = null)
    : IIndexedJobEnqueuer
{
    public string Enqueue(Expression<Action> methodCall, IndexPayload meta) =>
        EnqueueCore(Job.FromExpression(methodCall), meta);

    public string Enqueue<T>(Expression<Action<T>> methodCall, IndexPayload meta) =>
        EnqueueCore(Job.FromExpression(methodCall), meta);

    public string Enqueue(Expression<Func<Task>> methodCall, IndexPayload meta) =>
        EnqueueCore(Job.FromExpression(methodCall), meta);

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta) =>
        EnqueueCore(Job.FromExpression(methodCall), meta);

    public string Schedule(Expression<Action> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    public string Schedule<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    public string Schedule(Expression<Func<Task>> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    private string EnqueueCore(Job job, IndexPayload meta) =>
        CreateCore(job, meta, new EnqueuedState(meta.Queue ?? EnqueuedState.DefaultQueue));

    internal const string PreparationStateName = "Indexing";

    // Hangfire persists arbitrary non-final states but does not dispatch them to
    // workers. Publication completes before the transition to Enqueued/Scheduled,
    // so no fast worker can lose a terminal state/error against a missing index row.
    private sealed class PreparationState : IState
    {
        public string Name => PreparationStateName;
        public string Reason => "Publishing indexed job metadata";
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;
        public Dictionary<string, string> SerializeData() => [];
    }

    private string CreateCore(Job job, IndexPayload meta, IState state)
    {
        meta.EnsureValid();
        var createdAt = DateTime.UtcNow;
        var jobId = client.Create(job, new PreparationState());
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Hangfire did not create the indexed job");

        try
        {
            using (var conn = (storage ?? JobStorage.Current).GetConnection())
            {
                conn.SetJobParameter(jobId, JobParamKeys.OrgSlug, meta.OrgSlug);
                conn.SetJobParameter(jobId, JobParamKeys.DsSlug, meta.DsSlug);
                if (!string.IsNullOrWhiteSpace(meta.Path)) conn.SetJobParameter(jobId, JobParamKeys.Path, meta.Path);
                if (!string.IsNullOrWhiteSpace(meta.Hash)) conn.SetJobParameter(jobId, JobParamKeys.Hash, meta.Hash);
                if (!string.IsNullOrWhiteSpace(meta.UserId)) conn.SetJobParameter(jobId, JobParamKeys.UserId, meta.UserId);
                if (!string.IsNullOrWhiteSpace(meta.Queue)) conn.SetJobParameter(jobId, JobParamKeys.Queue, meta.Queue);
            }

            using var scope = sp.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            writer.UpsertOnEnqueueAsync(jobId, meta, createdAt, job.ToString()).GetAwaiter().GetResult();
            if (!client.ChangeState(jobId, state, PreparationStateName))
                throw new InvalidOperationException($"Cannot publish indexed job '{jobId}' from its preparation state");
            return jobId;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to publish indexed job {JobId}; cancelling the unpublished job", jobId);
            try
            {
                // The expected-state guard cannot cancel a job already running or
                // completed through an external state transition.
                if (client.ChangeState(jobId, new DeletedState { Reason = "Index publication failed" }, PreparationStateName))
                {
                    using var scope = sp.CreateScope();
                    scope.ServiceProvider.GetRequiredService<IJobIndexWriter>()
                        .UpdateStateAsync(jobId, DeletedState.StateName, DateTime.UtcNow).GetAwaiter().GetResult();
                }
            }
            catch (Exception cleanupError)
            {
                log.LogError(cleanupError, "Cannot cancel unpublished indexed job {JobId}", jobId);
            }
            throw;
        }
    }
}
