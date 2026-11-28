using System;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Services.Jobs;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The one-run-at-a-time filter on the in-memory storage (a semaphore per job method): a run that overlaps another one
/// ends without running its body and is recorded as Deleted (Hangfire's state of a run a filter cancels), not Failed. The MySQL session lock is checked in
/// <c>OneRunAtATimeMySqlIntegrationTests</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class OneRunAtATimeFilterTests
{
    private static OneRunAtATimeFilter NewFilter() =>
        new(new ConfigurationBuilder().Build(), NullLogger<OneRunAtATimeFilter>.Instance);

    [Test]
    public async Task OverlappingRuns_OnlyTheFirstEntersTheBody_AndALaterOneDoes()
    {
        using var storage = new InMemoryStorage();

        await OverlapProbeJob.AssertOverlappingRunsAreExcluded(NewFilter(), storage);
    }

    [Test]
    public async Task ARunThatFindsAnotherOne_EndsAsDeleted_NotFailed()
    {
        OverlapProbeJob.Reset();
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var states = new StateRecorder();
        using var server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            WorkerCount = 2,
            // Its own activator: the global one may be that of a host another test disposed
            Activator = new JobActivator(),
            FilterProvider = new JobFilterCollection { NewFilter(), states }
        }, storage);

        var first = client.Enqueue(() => OverlapProbeJob.Run());
        try
        {
            OverlapProbeJob.WaitEntered(TimeSpan.FromSeconds(30)).ShouldBeTrue("the first run enters the body");
            var second = client.Enqueue(() => OverlapProbeJob.Run());

            (await states.Final(second)).ShouldBe("Deleted", "a run a filter cancels ends as Deleted, not Failed");
            OverlapProbeJob.Bodies.ShouldBe(1, "the body ran once");
        }
        finally
        {
            // A run left waiting in the body would keep the lock from the tests after
            OverlapProbeJob.ReleaseOne();
        }

        (await states.Final(first)).ShouldBe("Succeeded");
    }

    /// <summary>Records the terminal state each job ends in, as Hangfire applies it.</summary>
    private sealed class StateRecorder : JobFilterAttribute, IApplyStateFilter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> _final = new();

        public Task<string> Final(string jobId) => _final.GetOrAdd(jobId, _ => new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.NewState is SucceededState or DeletedState or FailedState)
                _final.GetOrAdd(context.BackgroundJob.Id, _ => new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(context.NewState.Name);
        }

        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
