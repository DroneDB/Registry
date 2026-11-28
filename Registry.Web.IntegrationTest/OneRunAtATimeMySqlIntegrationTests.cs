using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NUnit.Framework;
using Registry.Web.Services.Jobs;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The session lock of <see cref="OneRunAtATimeFilter"/> on the production job storage: an overlapping run never
/// enters the body, which Hangfire's own <c>DisableConcurrentExecution</c> with a zero timeout does not achieve on the
/// MySQL storage (it takes its lock row whenever no row is younger than the timeout, so with 0 any run takes it), and
/// the lock of a session that dies is free again.
/// </summary>
[TestFixture]
public sealed class OneRunAtATimeMySqlIntegrationTests : HangfireMySqlTestBase
{
    private const string KeyPrefix = "job-run";

    [Test]
    public async Task OverlappingRuns_OnlyTheFirstEntersTheBody_AndALaterOneDoes()
    {
        var filter = new OneRunAtATimeFilter(Configuration(), NullLogger<OneRunAtATimeFilter>.Instance);

        await OverlapProbeJob.AssertOverlappingRunsAreExcluded(filter, Storage());
    }

    [Test]
    public async Task TheLock_IsFreeAgain_WhenTheSessionHoldingItDies()
    {
        // A host that dies takes its connection with it: the server drops the lock, no run is locked out for good
        const string resource = "Some.Job.Run";
        using var held = MySqlSessionLock.TryAcquire(Connection, resource, KeyPrefix, TimeSpan.Zero);
        held.ShouldNotBeNull();
        MySqlSessionLock.TryAcquire(Connection, resource, KeyPrefix, TimeSpan.Zero).ShouldBeNull("the lock is held");

        var database = new MySqlConnectionStringBuilder(Connection).Database;
        var name = MySqlSessionLock.DigestKey($"{database.Length}:{database}{resource}", KeyPrefix);
        var holder = await MariaDbServer.ScalarAsync(Connection, $"SELECT IS_USED_LOCK('{name}')");
        await MariaDbServer.ExecuteAsync(Connection, "KILL " + Convert.ToInt64(holder, CultureInfo.InvariantCulture));

        using var next = MySqlSessionLock.TryAcquire(Connection, resource, KeyPrefix, TimeSpan.FromSeconds(5));
        next.ShouldNotBeNull("the lock of a dead session is released");
    }

    [Test]
    public async Task ALease_StillExcludesAnotherAcquirer_AfterTheIdleTimeoutOfTheServerPassed()
    {
        // The session of a lease sends nothing while its run works: a server that drops idle sessions would free the lock
        // mid-run. The server here drops a session idle for 2 s, unless the lease raised its own idle timeout
        const string resource = "Some.Long.Job.Run";
        var original = Convert.ToInt64(await MariaDbServer.ScalarAsync(Connection, "SELECT @@global.wait_timeout"),
            CultureInfo.InvariantCulture);
        IDisposable? held;
        await MariaDbServer.ExecuteAsync(Connection, "SET GLOBAL wait_timeout = 2");
        try
        {
            held = MySqlSessionLock.TryAcquire(Connection, resource, KeyPrefix, TimeSpan.Zero);
        }
        finally
        {
            await MariaDbServer.ExecuteAsync(Connection, "SET GLOBAL wait_timeout = " + original);
        }

        using (held)
        {
            held.ShouldNotBeNull();
            // Nothing to await: the server's idle timer is what the test waits out (twice the timeout)
            new ManualResetEventSlim().Wait(TimeSpan.FromSeconds(4));

            MySqlSessionLock.TryAcquire(Connection, resource, KeyPrefix, TimeSpan.Zero)
                .ShouldBeNull("the lease is still held after the server's idle timeout");
        }
    }

    [Test]
    public void ALeaseThatCannotReleaseItsLock_DoesNotThrow_LogsIt_AndClosesItsSession()
    {
        // An exception out of the release would reach OnPerformed and mark a run whose body succeeded as Failed
        var session = new MySqlConnection(Connection);
        session.Open();
        var log = new RecordingLogger();

        var lease = new FailingRelease(session, "a-lock", log);

        Should.NotThrow(lease.Dispose);
        session.State.ShouldBe(System.Data.ConnectionState.Closed, "closing the session frees the lock on the server");
        log.Warnings.ShouldHaveSingleItem().ShouldContain("a-lock");
    }

    private sealed class FailingRelease(MySqlConnection session, string name, ILogger log)
        : MySqlSessionLock.Lease(session, name, log)
    {
        protected override void Release(MySqlConnection connection, string lockName) =>
            throw new InvalidOperationException("The release failed on an open session");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
