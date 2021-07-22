using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class BackgroundJobsProcessor : IBackgroundJobsProcessor
    {
        private readonly IBackgroundJobClient _client;

        public BackgroundJobsProcessor(IBackgroundJobClient client)
        {
            _client = client;
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

        public bool Requeue(string jobId) => _client.Delete(jobId);

        public JobStatus GetJobStatus(string jobId)
        {
            var details = JobStorage.Current.GetMonitoringApi().JobDetails(jobId);

            var lastState = details.History.LastOrDefault();

            if (lastState == null) return JobStatus.Unknown;

            return !Enum.TryParse(lastState.StateName, out JobStatus state) ? JobStatus.Unknown : state;
        }

        public string ContinueJobWith(string parentId, Expression<Action> methodCall,
            BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState) => _client.ContinueJobWith(parentId, methodCall, null, (Hangfire.JobContinuationOptions)options);

        public string ContinueJobWith<T>(string parentId, Expression<Action<T>> methodCall,
            BackgroundJobContinuationOptions options = BackgroundJobContinuationOptions.OnlyOnSucceededState) => _client.ContinueJobWith(parentId, methodCall, null, (Hangfire.JobContinuationOptions)options);
    }
}
