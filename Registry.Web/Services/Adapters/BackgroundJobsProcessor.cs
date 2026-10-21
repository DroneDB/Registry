using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.States;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Hangfire job enqueue/schedule/delete wrapper with indexed job support.
/// </summary>
public class BackgroundJobsProcessor : IBackgroundJobsProcessor
{
    private readonly IBackgroundJobClient _client;
    private readonly IIndexedJobEnqueuer _indexedEnqueuer;
    private readonly JobStorage _storage;

    public BackgroundJobsProcessor(IBackgroundJobClient client, IIndexedJobEnqueuer indexedEnqueuer, JobStorage storage)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _indexedEnqueuer = indexedEnqueuer ?? throw new ArgumentNullException(nameof(indexedEnqueuer));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public string Enqueue(Expression<Action> methodCall) => _client.Enqueue(methodCall);

    public string Enqueue(Expression<Func<Task>> methodCall) => _client.Enqueue(methodCall);

    public string Enqueue<T>(Expression<Action<T>> methodCall) => _client.Enqueue(methodCall);

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall) => _client.Enqueue(methodCall);

    public string Schedule(Expression<Action> methodCall, TimeSpan delay) => _client.Schedule(methodCall, delay);

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay) => _client.Schedule(methodCall, delay);

    public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay) => _client.Schedule(methodCall, delay);

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay) => _client.Schedule(methodCall, delay);

    public bool Delete(string jobId) => _client.Delete(jobId);

    /// <summary>
    /// Re-runs jobId under the same id by transitioning it back to <c>EnqueuedState</c>
    /// (same-id state transition, not delete). Failed-only by design: jobs in any other
    /// state are rejected. The live Hangfire state is authoritative for the guard —
    /// <c>JobIndex.CurrentState</c> is display-only because the state-filter write is async
    /// and may lag.
    /// </summary>
    /// <param name="jobId">The Hangfire job id to re-queue.</param>
    /// <returns>True when the job was re-queued; false when it no longer exists in Hangfire or is not in <c>Failed</c>.</returns>
    public bool Requeue(string jobId)
    {
        using var connection = _storage.GetConnection();
        var stateName = connection.GetStateData(jobId)?.Name;

        if (stateName == null) return false; // no such job in Hangfire (expired/purged)
        if (stateName != FailedState.StateName) return false; // Failed-only guard

        return _client.Requeue(jobId, stateName); // BackgroundJobClientExtensions.Requeue
    }

    public JobStatus GetJobStatus(string jobId)
    {
        using var connection = _storage.GetConnection();
        var stateName = connection.GetStateData(jobId)?.Name;
        return Enum.TryParse(stateName, out JobStatus state) ? state : JobStatus.Unknown;
    }

    public string ContinueJobWith(string parentId, Expression<Action> methodCall,
        BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState) => _client.ContinueJobWith(parentId, methodCall, (JobContinuationOptions)options);

    public string ContinueJobWith<T>(string parentId, Expression<Action<T>> methodCall,
        BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState) => _client.ContinueJobWith(parentId, methodCall, (JobContinuationOptions)options);

    // Indexed job methods - delegate to IIndexedJobEnqueuer
    public string EnqueueIndexed(Expression<Action> methodCall, IndexPayload meta) =>
        _indexedEnqueuer.Enqueue(methodCall, meta);

    public string EnqueueIndexed(Expression<Func<Task>> methodCall, IndexPayload meta) =>
        _indexedEnqueuer.Enqueue(methodCall, meta);

    public string EnqueueIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta) =>
        _indexedEnqueuer.Enqueue(methodCall, meta);

    public string EnqueueIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta) =>
        _indexedEnqueuer.Enqueue(methodCall, meta);

    // Indexed scheduled (delayed) job methods - delegate to IIndexedJobEnqueuer
    public string ScheduleIndexed(Expression<Action> methodCall, IndexPayload meta, TimeSpan delay) =>
        _indexedEnqueuer.Schedule(methodCall, meta, delay);

    public string ScheduleIndexed(Expression<Func<Task>> methodCall, IndexPayload meta, TimeSpan delay) =>
        _indexedEnqueuer.Schedule(methodCall, meta, delay);

    public string ScheduleIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) =>
        _indexedEnqueuer.Schedule(methodCall, meta, delay);

    public string ScheduleIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta, TimeSpan delay) =>
        _indexedEnqueuer.Schedule(methodCall, meta, delay);
}