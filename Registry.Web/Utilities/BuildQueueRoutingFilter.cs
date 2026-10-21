#nullable enable
using System;
using Hangfire.Common;
using Hangfire.States;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Utilities;

/// <summary>Hangfire queues and the server that processes each (REG-09).</summary>
public static class JobQueues
{
    /// <summary>Jobs that run native builds; served by <see cref="ProcessingBudget.HeavyWorkers"/> workers.</summary>
    public const string Builds = "builds";

    /// <summary>Heavy-task substrate jobs that do not run native builds.</summary>
    public const string Tasks = "tasks";

    public const string Default = EnqueuedState.DefaultQueue;

    /// <summary>Queues served by the light workers.</summary>
    public static readonly string[] Light = [Tasks, Default];
}

/// <summary>
/// Puts every job that runs a native build on the <see cref="JobQueues.Builds"/> queue, whose
/// worker count bounds how many builds run at once. Routing happens on every transition to
/// Enqueued (first enqueue, automatic retry, dashboard requeue), so a retried build cannot land
/// on the light workers. Heavy-task jobs that do not build go to <see cref="JobQueues.Tasks"/>;
/// other jobs keep the queue they were enqueued with.
/// </summary>
public sealed class BuildQueueRoutingFilter : JobFilterAttribute, IElectStateFilter
{
    private readonly Func<string, string, bool> _toolRunsNativeBuild;

    /// <param name="tools">The heavy tools: whether a tool runs a native build decides its queue.</param>
    public BuildQueueRoutingFilter(IHeavyToolRegistry tools)
        : this((toolId, version) => tools.Resolve(toolId, version)?.RunsNativeBuild == true)
    {
    }

    /// <param name="toolRunsNativeBuild">(toolId, toolVersion) => whether that heavy tool runs a native build.</param>
    public BuildQueueRoutingFilter(Func<string, string, bool> toolRunsNativeBuild)
    {
        _toolRunsNativeBuild = toolRunsNativeBuild ?? throw new ArgumentNullException(nameof(toolRunsNativeBuild));
        // After any filter that could replace the candidate state.
        Order = int.MaxValue;
    }

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is not EnqueuedState enqueued)
            return;

        var queue = QueueFor(context.BackgroundJob?.Job);
        if (queue != null)
            enqueued.Queue = queue;
    }

    /// <summary>The queue <paramref name="job"/> must run on, or null to keep the requested one.</summary>
    public string? QueueFor(Job? job)
    {
        if (job == null)
            return null;

        if (DdbJobDescriptor.Of(job) is { } ddbJob)
            return ddbJob.RunsNativeBuild ? JobQueues.Builds : null;

        if (job.Type == typeof(HeavyTaskJobWrapper) && job.Method.Name == nameof(HeavyTaskJobWrapper.Run))
        {
            return job.Args.Count >= 2 && job.Args[0] is string toolId && job.Args[1] is string version &&
                   _toolRunsNativeBuild(toolId, version)
                ? JobQueues.Builds
                : JobQueues.Tasks;
        }

        return null;
    }
}
