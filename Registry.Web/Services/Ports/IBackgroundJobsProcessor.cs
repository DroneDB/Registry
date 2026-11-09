using System;
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
    public string EnqueueIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta);
    public string EnqueueIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta);
    public string ScheduleIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay);
}

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