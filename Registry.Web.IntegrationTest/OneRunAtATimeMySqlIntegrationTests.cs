using System;
using System.Globalization;
using System.Threading.Tasks;
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
}
