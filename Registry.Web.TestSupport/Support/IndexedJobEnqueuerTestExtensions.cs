using System;
using System.Linq.Expressions;
using Hangfire.Common;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test.Support;

/// <summary>Test shorthands over <see cref="IndexedJobEnqueuer"/>: jobs that call a static method.</summary>
public static class IndexedJobEnqueuerTestExtensions
{
    public static string Enqueue(this IndexedJobEnqueuer enqueuer, Expression<Action> methodCall, IndexPayload meta) =>
        enqueuer.EnqueueCore(Job.FromExpression(methodCall), meta);

    public static string Schedule(this IndexedJobEnqueuer enqueuer, Expression<Action> methodCall, IndexPayload meta,
        TimeSpan delay) => enqueuer.ScheduleCore(Job.FromExpression(methodCall), meta, delay);
}
