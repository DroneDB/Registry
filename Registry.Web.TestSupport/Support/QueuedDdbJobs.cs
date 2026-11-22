using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Moq;
using Registry.Web.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.Support;

/// <summary>A dataset job queued through <see cref="QueueDdbJobs"/>, its arguments as Hangfire serializes them.</summary>
/// <param name="Method">The <see cref="DdbJobs"/> method the job runs (Build, RebuildInBackground).</param>
/// <param name="Path">The entry, or null for a build of the whole dataset.</param>
/// <param name="Force">The force flag of a Build; a background rebuild never forces.</param>
/// <param name="Meta">The job index record of the job (its parent task, its user).</param>
public sealed record QueuedDdbJob(string Method, string OrgSlug, Guid InternalRef, string? Path, bool Force,
    IndexPayload Meta);

public static class QueuedDdbJobs
{
    /// <summary>
    /// Jobs (strict unless <paramref name="behavior"/> says otherwise) that record every
    /// <c>EnqueueIndexed&lt;DdbJobs&gt;</c> call in <paramref name="queued"/>, evaluating its arguments the way
    /// Hangfire serializes them; the job ids are "job-1", "job-2"...
    /// </summary>
    public static Mock<IBackgroundJobsProcessor> Capture(List<QueuedDdbJob> queued,
        MockBehavior behavior = MockBehavior.Strict)
    {
        var jobs = new Mock<IBackgroundJobsProcessor>(behavior);
        jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Returns<Expression<Action<DdbJobs>>, IndexPayload>((expr, meta) =>
            {
                var call = (MethodCallExpression)expr.Body;
                object? Eval(int i) => Expression.Lambda(call.Arguments[i]).Compile().DynamicInvoke();
                // Build(org, ref, path, force, context); RebuildInBackground(org, ref, path, context) never forces
                var hasForce = call.Method.Name == nameof(DdbJobs.Build);
                queued.Add(new QueuedDdbJob(call.Method.Name, (string)Eval(0)!, (Guid)Eval(1)!, (string?)Eval(2),
                    hasForce && (bool)Eval(3)!, meta));
                Eval(call.Arguments.Count - 1).ShouldBeNull(); // PerformContext is injected by Hangfire at run time
                return $"job-{queued.Count}";
            });
        return jobs;
    }
}
