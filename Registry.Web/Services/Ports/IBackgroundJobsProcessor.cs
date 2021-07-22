using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire.States;

namespace Registry.Web.Services.Ports
{
    public interface IBackgroundJobsProcessor
    {
        public string Enqueue(Expression<Action> methodCall);
        public string Enqueue(Expression<Func<Task>> methodCall);
        public string Enqueue<T>(Expression<Action<T>> methodCall);
        public string Enqueue<T>(Expression<Func<T, Task>> methodCall);

        public string Schedule(Expression<Action> methodCall, TimeSpan delay);
        public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay);
        public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay);
        public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay);

        public bool Delete(string jobId);
        public bool Requeue(string jobId);

        public JobStatus GetJobStatus(string jobId);

        public string ContinueJobWith(string parentId, Expression<Action> methodCall, BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState);
        public string ContinueJobWith<T>(string parentId, Expression<Action<T>> methodCall, BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState);
    }

    public enum BackgroundJobContinuationOptions
    {
        OnAnyFinishedState,
        OnlyOnSucceededState,
    }

    public enum JobStatus
    {
        Unknown,
        Succeeded,
        Enqueued,
        Deleted,
        Failed
    }
}
