using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Adapters;

public class SimpleBackgroundJobsProcessor : IBackgroundJobsProcessor
{

    private readonly Dictionary<int, JobStatus> _jobs = new();

    private int GetNewId()
    {
        return _jobs.Count > 0 ? (_jobs.Keys.Max() + 1) : 0;
    }

    public string Enqueue(Expression<Action> methodCall)
    {
        var action = methodCall.Compile();

        var newId = GetNewId();

        try
        {
            action();
            _jobs.Add(newId, JobStatus.Succeeded);

        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _jobs.Add(newId, JobStatus.Failed);

        }

        return newId.ToString();

    }

    public string Enqueue(Expression<Func<Task>> methodCall)
    {
        var action = methodCall.Compile();

        var newId = GetNewId();

        try
        {
            var task = action();
            task.Wait();
            _jobs.Add(newId, JobStatus.Succeeded);

        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _jobs.Add(newId, JobStatus.Failed);

        }

        return newId.ToString();
    }

    public string Enqueue<T>(Expression<Action<T>> methodCall)
    {
        throw new NotImplementedException();
    }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall)
    {
        throw new NotImplementedException();
    }

    public string Schedule(Expression<Action> methodCall, TimeSpan delay)
    {
        throw new NotImplementedException();
    }

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay)
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
        throw new NotImplementedException();
    }

    public bool Requeue(string jobId)
    {
        throw new NotImplementedException();
    }

    public JobStatus GetJobStatus(string jobId)
    {
        return !_jobs.TryGetValue(int.Parse(jobId), out var status) ? JobStatus.Unknown : status;
    }

    public string ContinueJobWith(string parentId, Expression<Action> methodCall,
        BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState)
    {

        switch (options)
        {
            case BackgroundJobContinuationOptions.OnAnyFinishedState:

                return Enqueue(methodCall);

            case BackgroundJobContinuationOptions.OnlyOnSucceededState:

                if (GetJobStatus(parentId) == JobStatus.Succeeded)
                    return Enqueue(methodCall);

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options, null);
        }

        return null;
    }

    public string ContinueJobWith<T>(string parentId, Expression<Action<T>> methodCall,
        BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState)
    {
        throw new NotImplementedException();
    }

    // Indexed job methods - for testing, we just execute immediately like regular Enqueue
    public string EnqueueIndexed(Expression<Action> methodCall, IndexPayload meta)
    {
        // For testing purposes, we ignore the IndexPayload and just execute the job
        return Enqueue(methodCall);
    }

    public string EnqueueIndexed(Expression<Func<Task>> methodCall, IndexPayload meta)
    {
        // For testing purposes, we ignore the IndexPayload and just execute the job
        return Enqueue(methodCall);
    }

    public string EnqueueIndexed<T>(Expression<Action<T>> methodCall, IndexPayload meta)
    {
        // For now, we can't easily execute generic methods in testing, so we return a fake job ID
        var newId = GetNewId();
        _jobs.Add(newId, JobStatus.Succeeded);
        return newId.ToString();
    }

    public string EnqueueIndexed<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta)
    {
        // For now, we can't easily execute generic methods in testing, so we return a fake job ID
        var newId = GetNewId();
        _jobs.Add(newId, JobStatus.Succeeded);
        return newId.ToString();
    }
}