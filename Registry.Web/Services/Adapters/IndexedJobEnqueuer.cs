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
/// Publishes metadata and JobIndex before making an indexed Hangfire job runnable.
/// </summary>
public class IndexedJobEnqueuer(IBackgroundJobClient client, IServiceScopeFactory scopes,
    ILogger<IndexedJobEnqueuer> log, JobStorage storage)
    : IIndexedJobEnqueuer
{
    public string Enqueue<T>(Expression<Action<T>> methodCall, IndexPayload meta) =>
        EnqueueCore(Job.FromExpression(methodCall), meta);

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta) =>
        EnqueueCore(Job.FromExpression(methodCall), meta);

    public string Schedule<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) =>
        ScheduleCore(Job.FromExpression(methodCall), meta, delay);

    internal string EnqueueCore(Job job, IndexPayload meta) =>
        CreateCore(job, meta, createdAt => IndexedJobPublicationRecord.ForImmediate(meta, createdAt,
            meta.Queue ?? EnqueuedState.DefaultQueue));

    internal string ScheduleCore(Job job, IndexPayload meta, TimeSpan delay) =>
        CreateCore(job, meta, createdAt => IndexedJobPublicationRecord.ForScheduled(meta, createdAt, delay));

    internal const string PreparationStateName = IndexingState.StateName;

    /// <summary>
    /// Creates the job in <see cref="IndexingState"/> carrying its publication record, then publishes it.
    /// Only a failure up to the creation commit reaches the caller ("not created"). Once the job exists
    /// it is never cancelled here: publication failures are logged, the job id is returned and
    /// IndexingStateHandler's recovery-set member lets recovery finish the publication.
    /// </summary>
    private string CreateCore(Job job, IndexPayload meta, Func<DateTime, IndexedJobPublicationRecord> recordFor)
    {
        meta.EnsureValid();
        var record = recordFor(DateTime.UtcNow);
        var invalid = record.Validate();
        if (invalid != null)
            throw new ArgumentException($"Invalid indexed job publication: {invalid}", nameof(meta));

        // Without the handler a stranded job would never enter the recovery set.
        if (!IndexingStateHandler.IsRegistered())
            throw new InvalidOperationException(
                $"{nameof(IndexingStateHandler)} is not registered in GlobalStateHandlers; refusing to create an indexed job");

        var jobId = client.Create(job, new IndexingState(record));
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Hangfire did not create the indexed job");

        try
        {
            using var scope = scopes.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            var result = IndexedJobPublication.PublishAsync(jobId, record, storage, client,
                writer, job.ToString(), log).GetAwaiter().GetResult();

            if (result.Outcome != IndexedJobPublicationOutcome.Published)
                log.LogWarning("Indexed job {JobId} was not published by its creator (outcome {Outcome}, state {State}); " +
                               "recovery will complete or classify it", jobId, result.Outcome, result.ActualState);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to publish indexed job {JobId} after creation; recovery will complete it", jobId);
        }

        return jobId;
    }
}
