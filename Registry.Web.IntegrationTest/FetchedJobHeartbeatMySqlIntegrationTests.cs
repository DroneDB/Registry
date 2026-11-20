using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Services.Jobs;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="FetchedJobHeartbeat"/> on the production job storage: the MySQL storage hands a queue row to another
/// worker when it was fetched longer ago than the invisibility timeout, even while the first worker still runs the job.
/// Two servers share a storage with a 2 s timeout and run a job of 6 s.
/// </summary>
[TestFixture]
public sealed class FetchedJobHeartbeatMySqlIntegrationTests : HangfireMySqlTestBase
{
    [Test]
    public async Task ALongJob_IsFetchedOnce_WhileItsQueueRowIsRenewed()
    {
        var heartbeat = new FetchedJobHeartbeat(Configuration(), NullLogger<FetchedJobHeartbeat>.Instance,
            TimeSpan.FromSeconds(1), TablesPrefix);

        (await RunLongJobOnTwoServers(heartbeat)).ShouldBe(1, "no other worker fetched the job again");
    }

    [Test]
    public async Task ALongJob_IsFetchedAgain_WithoutTheHeartbeat()
    {
        (await RunLongJobOnTwoServers(null)).ShouldBeGreaterThan(1,
            "past the invisibility timeout another worker takes the job: the control of the test above");
    }

    // The times the body was entered
    private async Task<int> RunLongJobOnTwoServers(IServerFilter? filter)
    {
        LongJob.Reset();
        var storage = Storage(TimeSpan.FromSeconds(2));
        var client = new BackgroundJobClient(storage);
        var filters = new JobFilterCollection();
        if (filter is not null)
            filters.Add(filter);

        BackgroundJobServer Server() => new(new BackgroundJobServerOptions
        {
            WorkerCount = 1,
            // Its own activator: the global one may be that of a host another test disposed
            Activator = new JobActivator(),
            FilterProvider = filters
        }, storage);

        using var first = Server();
        using var second = Server();
        var id = client.Enqueue(() => LongJob.Run());

        await Eventually.Until(() => Task.FromResult(
                storage.GetMonitoringApi().JobDetails(id)?.History.FirstOrDefault()?.StateName == "Succeeded"
                && !LongJob.IsRunning),
            "the long job succeeded", TimeSpan.FromSeconds(60));

        return LongJob.Entered;
    }

    /// <summary>A job that outlasts the invisibility timeout of the test.</summary>
    public static class LongJob
    {
        // Never released: the job just waits for the 6 s to pass
        private static readonly SemaphoreSlim NeverReleased = new(0);
        private static int _entered;
        private static int _running;

        public static int Entered => Volatile.Read(ref _entered);

        public static bool IsRunning => Volatile.Read(ref _running) > 0;

        public static void Reset()
        {
            Volatile.Write(ref _entered, 0);
            Volatile.Write(ref _running, 0);
        }

        public static void Run()
        {
            Interlocked.Increment(ref _entered);
            Interlocked.Increment(ref _running);
            try { NeverReleased.Wait(TimeSpan.FromSeconds(6)); }
            finally { Interlocked.Decrement(ref _running); }
        }
    }
}
