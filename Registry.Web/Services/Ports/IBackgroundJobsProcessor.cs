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

    public bool Delete(string jobId);
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