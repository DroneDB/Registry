using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Adapters;

/// <summary>
/// In-memory <see cref="IBackgroundJobsProcessor"/> for tests that do not need a job server: a job is
/// recorded with the status it would end in, and none of them runs.
/// </summary>
public class SimpleBackgroundJobsProcessor : IBackgroundJobsProcessor
{
    private readonly Dictionary<int, JobStatus> _jobs = new();

    private int GetNewId()
    {
        return _jobs.Count > 0 ? (_jobs.Keys.Max() + 1) : 0;
    }

    private string Record(JobStatus status)
    {
        var newId = GetNewId();
        _jobs.Add(newId, status);
        return newId.ToString(CultureInfo.InvariantCulture);
    }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall)
    {
        throw new NotImplementedException();
    }

    public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay)
    {
        throw new NotImplementedException();
    }

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay)
    {
        throw new NotImplementedException();
    }

    public bool Delete(string jobId)
    {
        if (!_jobs.TryGetValue(int.Parse(jobId, CultureInfo.InvariantCulture), out var status))
            return false;

        if (status is JobStatus.Processing or JobStatus.Succeeded or JobStatus.Failed)
            _jobs[int.Parse(jobId, CultureInfo.InvariantCulture)] = JobStatus.Deleted;

        return true;
    }

    // Mirrors the production contract: flipped to Enqueued only from Failed (Failed-only guard).
    public bool Requeue(string jobId)
    {
        if (!_jobs.TryGetValue(int.Parse(jobId, CultureInfo.InvariantCulture), out var status))
            return false;

        if (status != JobStatus.Failed)
            return false;

        _jobs[int.Parse(jobId, CultureInfo.InvariantCulture)] = JobStatus.Enqueued;
        return true;
    }

    public JobStatus GetJobStatus(string jobId)
    {
        return !_jobs.TryGetValue(int.Parse(jobId, CultureInfo.InvariantCulture), out var status) ? JobStatus.Unknown : status;
    }

    public string EnqueueIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta) => Record(JobStatus.Succeeded);

    public string EnqueueIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta) => Record(JobStatus.Succeeded);

    // A delayed job is a future one: registered as Scheduled
    public string ScheduleIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) =>
        Record(JobStatus.Scheduled);
}
