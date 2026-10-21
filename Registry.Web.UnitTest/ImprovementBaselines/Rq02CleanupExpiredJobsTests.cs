#nullable enable
using Registry.Web.Services.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Utilities;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 3 (<c>CleanupExpiredJobs</c>): the daily housekeeping removed every recurring job whose
/// last run failed and that was created more than 30 days ago, including the application's own
/// recurring jobs (re-created only at the next start), and it compared the UTC <c>CreatedAt</c> with
/// local <c>DateTime.Now</c>. After the fix the IDs in <see cref="RecurringJobIds.Owned"/> (the same
/// constants <see cref="HangfireJobsInitializer"/> registers) are never removed, and the 30-day
/// cutoff comes from a <see cref="TimeProvider"/> in UTC.
/// <para>Covered with Hangfire in-memory storage and a fake clock.</para>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq02CleanupExpiredJobsTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq02CleanupExpiredJobsTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private InMemoryStorage _storage = null!;
    private FakeTime _time = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new InMemoryStorage();
        _time = new FakeTime(Now);
    }

    [TearDown]
    public void TearDown() => _storage.Dispose();

    // Target of the seeded jobs; never executed (no server runs). No retries, so the global
    // AutomaticRetry filter does not turn the seeded FailedState into a ScheduledState.
    [AutomaticRetry(Attempts = 0)]
    public static void Noop() { }

    [Test]
    public void OwnedRecurringJobs_WithAFailedLastRun_Survive()
    {
        foreach (var id in RecurringJobIds.Owned)
            SeedRecurring(id, Now.AddDays(-90), lastRunFailed: true);

        Cleanup();

        RecurringIds().ShouldBe(RecurringJobIds.Owned, ignoreOrder: true);
    }

    [Test]
    public void ForeignStaleRecurringJob_WithAFailedLastRun_IsRemoved()
    {
        SeedRecurring(RecurringJobIds.CleanupExpiredJobs, Now.AddDays(-90), lastRunFailed: true);
        SeedRecurring("user-job", Now.AddDays(-90), lastRunFailed: true);

        Cleanup();

        RecurringIds().ShouldBe([RecurringJobIds.CleanupExpiredJobs]);
    }

    [Test]
    public void ForeignStaleRecurringJob_WithoutAFailedLastRun_IsKept()
    {
        SeedRecurring("user-job", Now.AddDays(-90), lastRunFailed: false);

        Cleanup();

        RecurringIds().ShouldBe(["user-job"]);
    }

    [Test]
    public void ThirtyDayCutoff_IsTakenFromTheTimeProvider_InUtc()
    {
        SeedRecurring("just-outside", Now.AddDays(-30).AddMinutes(-1), lastRunFailed: true);
        SeedRecurring("just-inside", Now.AddDays(-30).AddMinutes(1), lastRunFailed: true);

        Cleanup();

        RecurringIds().ShouldBe(["just-inside"]);
    }

    [Test]
    public async Task Initializer_RegistersExactlyTheOwnedIds()
    {
        await new HangfireJobsInitializer(new RecurringJobManager(_storage),
                Microsoft.Extensions.Options.Options.Create(new JobScheduleOptions()),
                NullLogger<HangfireJobsInitializer>.Instance)
            .ExecuteAsync(CancellationToken.None);

        RecurringIds().ShouldBe(RecurringJobIds.Owned, ignoreOrder: true);
    }

    private void Cleanup() =>
        new ExpiredRecurringJobsSweep(_storage, _time, NullLogger<ExpiredRecurringJobsSweep>.Instance)
            .Run(null, CancellationToken.None);

    private string[] RecurringIds()
    {
        using var connection = _storage.GetConnection();
        return connection.GetRecurringJobs().Select(j => j.Id).ToArray();
    }

    private void SeedRecurring(string id, DateTime createdAtUtc, bool lastRunFailed)
    {
        new RecurringJobManager(_storage).AddOrUpdate(id, Job.FromExpression(() => Noop()), Cron.Never());

        IState lastState = lastRunFailed
            ? new FailedState(new InvalidOperationException("boom"))
            : new SucceededState(null, 0, 0);
        var lastJobId = new BackgroundJobClient(_storage).Create(() => Noop(), lastState);

        using var connection = _storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash($"recurring-job:{id}", new Dictionary<string, string>
        {
            ["CreatedAt"] = JobHelper.SerializeDateTime(createdAtUtc),
            ["LastJobId"] = lastJobId
        });
        transaction.Commit();
    }
}
