using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
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

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall) => _client.Enqueue(methodCall);

    public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay) => _client.Schedule(methodCall, delay);

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay) => _client.Schedule(methodCall, delay);

    public bool Delete(string jobId) => _client.Delete(jobId);

    public bool Delete(string jobId, string fromState) => _client.Delete(jobId, fromState);

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

    public JobStatus GetJobStatus(string jobId) =>
        ReadStore(connection => Enum.TryParse(connection.GetStateData(jobId)?.Name, out JobStatus state)
            ? state
            : JobStatus.Unknown, JobStatus.Unknown);

    public StoredJob? GetStoredJob(string jobId) =>
        ReadStore(connection => connection.GetJobData(jobId)?.Job is { } job
            ? new StoredJob(job.Type, job.Method.Name, job.Args)
            : null, null);

    public string? GetJobParameter(string jobId, string name) =>
        ReadStore(connection => connection.GetJobParameter(jobId, name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null, null);

    // A read of the job store. An id the store cannot read (a number of the earlier numbering in a GUID store, or the
    // other way round) or a stored job whose type cannot be loaded gives the answer of a job the store does not have
    private T ReadStore<T>(Func<IStorageConnection, T> read, T notThere)
    {
        try
        {
            using var connection = _storage.GetConnection();
            return read(connection);
        }
        catch (FormatException)
        {
            return notThere;
        }
        catch (ArgumentException)
        {
            return notThere;
        }
        catch (InvalidOperationException)
        {
            return notThere;
        }
    }

    public string EnqueueIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta) =>
        _indexedEnqueuer.Enqueue(methodCall, meta);

    public string EnqueueIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta) =>
        _indexedEnqueuer.Enqueue(methodCall, meta);

    public string ScheduleIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) =>
        _indexedEnqueuer.Schedule(methodCall, meta, delay);
}