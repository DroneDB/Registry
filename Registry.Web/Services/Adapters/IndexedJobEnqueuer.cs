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

/// <summary>
/// Parameter names used to pass metadata through Hangfire job arguments.
/// </summary>
public static class JobParamKeys
{
    public const string OrgSlug = "orgSlug";
    public const string DsSlug = "dsSlug";
    public const string Path = "path";
    public const string Hash = "hash";
    public const string UserId = "userId";
    public const string Queue = "queue"; // Optional
}

/// <summary>
/// Indexed Hangfire enqueue/schedule wrapper that writes JobIndex rows alongside job creation.
/// </summary>
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

    public string Schedule(Expression<Action> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    public string Schedule<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    public string Schedule(Expression<Func<Task>> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta, TimeSpan delay) =>
        CreateCore(Job.FromExpression(methodCall), meta, new ScheduledState(delay));

    private string EnqueueCore(Job job, IndexPayload meta) =>
        CreateCore(job, meta, new EnqueuedState(meta.Queue ?? EnqueuedState.DefaultQueue));

    private string CreateCore(Job job, IndexPayload meta, IState state)
    {
        meta.EnsureValid();
        var createdAt = DateTime.UtcNow;
        var queue = meta.Queue;

        var jobId = client.Create(job, state);

        // Set Job Parameters to track metadata in Hangfire storage
        try
        {
            using var conn = JobStorage.Current.GetConnection();
            conn.SetJobParameter(jobId, JobParamKeys.OrgSlug, meta.OrgSlug);
            conn.SetJobParameter(jobId, JobParamKeys.DsSlug, meta.DsSlug);
            if (!string.IsNullOrWhiteSpace(meta.Path)) conn.SetJobParameter(jobId, JobParamKeys.Path, meta.Path);
            if (!string.IsNullOrWhiteSpace(meta.Hash)) conn.SetJobParameter(jobId, JobParamKeys.Hash, meta.Hash);
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