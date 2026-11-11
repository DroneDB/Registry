using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Registry.Web.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Hangfire enqueue/schedule/delete/requeue interface for background job processing.
/// </summary>
public interface IBackgroundJobsProcessor
{
    public string Enqueue<T>(Expression<Func<T, Task>> methodCall);
    public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay);
    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay);

    public bool Delete(string jobId);

    /// <summary>
    /// Deletes the job only while it is in the state <paramref name="fromState"/>: the check and the change are one
    /// atomic state change of the storage, so a job deleted from a waiting state was never started by a worker.
    /// </summary>
    /// <returns>True when the job was in <paramref name="fromState"/> and is deleted.</returns>
    public bool Delete(string jobId, string fromState);
    public bool Requeue(string jobId);

    public JobStatus GetJobStatus(string jobId);

    /// <summary>The method and arguments of a stored job, or null when the store has no such job or cannot read it.</summary>
    public StoredJob? GetStoredJob(string jobId);

    /// <summary>A parameter of a stored job (as <c>PublishedOutputMarker</c> sets it), or null.</summary>
    public string? GetJobParameter(string jobId, string name);
    public string EnqueueIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta);
    public string EnqueueIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta);
    public string ScheduleIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay);
}

/// <summary>A job as the job store keeps it: the class and method it runs and their arguments.</summary>
public sealed record StoredJob(Type Type, string Method, IReadOnlyList<object?> Args);

public enum JobStatus
{
    Unknown,
    Scheduled,
    Processing,
    Succeeded,
    Enqueued,
    Deleted,
    Failed,
    Awaiting
}