#nullable enable
using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

public static class JobParamKeys
{
    public const string OrgSlug = "orgSlug";
    public const string DsSlug = "dsSlug";
    public const string Path = "path";
    public const string UserId = "userId";
    public const string Queue = "queue"; // Optional
}

public class IndexedJobEnqueuer(IBackgroundJobClient client, IServiceProvider sp, ILogger<IndexedJobEnqueuer> log)
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

    private string EnqueueCore(Job job, IndexPayload meta)
    {
        meta.EnsureValid();
        var createdAt = DateTime.UtcNow;
        var queue = meta.Queue;
        
        var jobId = client.Create(job, new EnqueuedState(queue ?? EnqueuedState.DefaultQueue));

        // Set Job Parameters to track metadata in Hangfire storage
        try
        {
            using var conn = JobStorage.Current.GetConnection();
            conn.SetJobParameter(jobId, JobParamKeys.OrgSlug, meta.OrgSlug);
            conn.SetJobParameter(jobId, JobParamKeys.DsSlug, meta.DsSlug);
            if (!string.IsNullOrWhiteSpace(meta.Path)) conn.SetJobParameter(jobId, JobParamKeys.Path, meta.Path);
            if (!string.IsNullOrWhiteSpace(meta.UserId)) conn.SetJobParameter(jobId, JobParamKeys.UserId, meta.UserId);
            if (!string.IsNullOrWhiteSpace(queue)) conn.SetJobParameter(jobId, JobParamKeys.Queue, queue);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to set Job Parameters for Job {JobId}", jobId);
        }

        // Write/update the application index
        try
        {
            using var scope = sp.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            var methodDisplay = job.ToString();
            writer.UpsertOnEnqueueAsync(jobId, meta, createdAt, methodDisplay).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to write JobIndex for Job {JobId}", jobId);
        }

        return jobId;
    }
}