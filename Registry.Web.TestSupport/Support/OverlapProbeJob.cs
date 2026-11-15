using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Registry.Web.Services.Jobs;
using Shouldly;

namespace Registry.Web.Test.Support;

/// <summary>
/// A job marked <see cref="OneRunAtATimeAttribute"/> whose body waits until the test lets it end, and the check that
/// two overlapping runs of it never both enter the body, whichever storage the lock of the filter works on. The state
/// is static (Hangfire builds the job object), so the tests using it do not run in parallel.
/// </summary>
[OneRunAtATime]
public sealed class OverlapProbeJob
{
    private static readonly SemaphoreSlim Entering = new(0);
    private static readonly SemaphoreSlim Leaving = new(0);
    private static int _bodies;

    /// <summary>How many times a run entered the body since <see cref="Reset"/>.</summary>
    public static int Bodies => Volatile.Read(ref _bodies);

    public static void Reset()
    {
        while (Entering.Wait(0)) { }
        while (Leaving.Wait(0)) { }
        Volatile.Write(ref _bodies, 0);
    }

    public static Job Job() => Hangfire.Common.Job.FromExpression(() => Run());

    /// <summary>Lets one run waiting in the body end.</summary>
    public static void ReleaseOne() => Leaving.Release();

    /// <summary>Waits until a run is in the body.</summary>
    public static bool WaitEntered(TimeSpan timeout) => Entering.Wait(timeout);

    public static void Run()
    {
        Interlocked.Increment(ref _bodies);
        Entering.Release();
        Leaving.Wait();
    }

    /// <summary>
    /// Performs the job three times with <paramref name="filter"/> through Hangfire's own performer: a run that is in
    /// the body; a second one that overlaps it and so never enters the body; a third after the first ended, which does.
    /// </summary>
    public static async Task AssertOverlappingRunsAreExcluded(IServerFilter filter, JobStorage storage)
    {
        Reset();
        var filters = new JobFilterCollection { filter };
        var performer = new BackgroundJobPerformer(filters, new JobActivator(), TaskScheduler.Default);

        void Perform(string id)
        {
            using var connection = storage.GetConnection();
            var context = new PerformContext(storage, connection, new BackgroundJob(id, Job(), DateTime.UtcNow),
                new JobCancellationToken(false));
            performer.Perform(context);
        }

        var runs = new System.Collections.Generic.List<Task>();
        try
        {
            runs.Add(Task.Run(() => Perform("first")));
            WaitEntered(TimeSpan.FromSeconds(30)).ShouldBeTrue("the first run enters the body");

            // On its own thread: a second run that did enter the body would wait there, and the test must fail, not hang
            var second = Task.Run(() => Perform("second"));
            runs.Add(second);
            second.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("the second run ends without waiting in the body");
            Bodies.ShouldBe(1, "the second run, started while the first performs, never enters the body");

            ReleaseOne();
            await runs[0];

            runs.Add(Task.Run(() => Perform("third")));
            WaitEntered(TimeSpan.FromSeconds(30)).ShouldBeTrue("a run started after the first ended enters the body");
            Bodies.ShouldBe(2);
        }
        finally
        {
            // A failed assertion must not leave a run waiting in the body: it would keep its lock from the tests after
            Leaving.Release(runs.Count);
            await Task.WhenAll(runs);
        }
    }
}
