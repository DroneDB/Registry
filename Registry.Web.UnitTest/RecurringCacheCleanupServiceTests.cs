using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Initialization;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The nightly cleanup of libddb's cache: the job hands libddb the ages of the settings, logs what each kind freed and
/// the totals, stops when its job is canceled, and is scheduled by the initializer with its own cron (or removed).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RecurringCacheCleanupServiceTests"
/// </summary>
[TestFixture]
public class RecurringCacheCleanupServiceTests
{
    /// <summary>The fake libddb of the tests: records the call and answers with <see cref="Result"/>.</summary>
    private sealed class CleanupWrapper : FakeDdbWrapper
    {
        public CacheCleanupResult Result { get; set; } = new();
        public List<(CacheCleanupOptions? Options, CancellationToken Token)> Calls { get; } = [];

        public override CacheCleanupResult CleanupCache(CacheCleanupOptions? options, CancellationToken cancellationToken)
        {
            Calls.Add((options, cancellationToken));
            // As libddb: a canceled token stops the cleanup at its next entry
            if (cancellationToken.IsCancellationRequested)
                throw new DdbCanceledException("DDBCleanupCache: canceled");
            return Result;
        }
    }

    private const long MiB = 1024 * 1024;

    private static CacheCleanupResult Removed(params (string Kind, long Entries, long Bytes)[] kinds) => new()
    {
        Kinds = kinds.ToDictionary(k => k.Kind, k => new CacheCleanupCount { Entries = k.Entries, Bytes = k.Bytes })
    };

    private static RecurringCacheCleanupService Service(CleanupWrapper ddb, JobScheduleOptions schedule,
        ILogger<RecurringCacheCleanupService>? logger = null) =>
        new(ddb, Microsoft.Extensions.Options.Options.Create(schedule), logger ?? NullLogger<RecurringCacheCleanupService>.Instance);

    [Test]
    public void TheAgesOfTheSettings_AreTheOptionsOfTheCleanup_WithItsToken()
    {
        var ddb = new CleanupWrapper();
        using var cts = new CancellationTokenSource();

        Service(ddb, new JobScheduleOptions
        {
            CacheCleanupTileDays = 1, CacheCleanupDownloadDays = 0, CacheCleanupDsmDays = 90, CacheCleanupTempDays = 36500
        }).CleanupCache(null, cts.Token);

        var call = ddb.Calls.ShouldHaveSingleItem();
        call.Options.ShouldBe(new CacheCleanupOptions(TileDays: 1, DownloadDays: 0, DsmDays: 90, TempDays: 36500));
        call.Token.ShouldBe(cts.Token);
    }

    [Test]
    public void ByDefault_TheAgesAreThoseOfLibddb_AndNothingIsADryRun()
    {
        var ddb = new CleanupWrapper();

        Service(ddb, new JobScheduleOptions()).CleanupCache(null, CancellationToken.None);

        ddb.Calls.ShouldHaveSingleItem().Options.ShouldBe(new CacheCleanupOptions(5, 7, 30, 2, DryRun: false));
    }

    [Test]
    public void WhatEachKindFreed_IsLogged_WithTheTotals()
    {
        var ddb = new CleanupWrapper
        {
            Result = Removed(("tiles", 120, 300 * MiB), ("downloads", 2, MiB / 2), ("dsm", 0, 0), ("artefacts", 1, 4096 * MiB))
        };
        var log = new CapturingLogger<RecurringCacheCleanupService>();

        Service(ddb, new JobScheduleOptions(), log).CleanupCache(null, CancellationToken.None);

        var messages = log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        messages[0].ShouldContain("tiles older than 5 days");
        messages.ShouldContain(m => m.Contains("Removed 120 tiles entries") && m.Contains("300.0 MiB"));
        messages.ShouldContain(m => m.Contains("Removed 2 downloads entries") && m.Contains("0.5 MiB"));
        messages.ShouldContain(m => m.Contains("Removed 1 artefacts entries") && m.Contains("4096.0 MiB"));
        messages.ShouldNotContain(m => m.Contains(" dsm entries"), "a kind that removed nothing is not listed");
        messages[^1].ShouldContain("123 entries removed");
        messages[^1].ShouldContain("4396.5 MiB freed");
    }

    [Test]
    public void ACleanupThatRemovesNothing_LogsOnlyTheStartAndTheTotals()
    {
        var ddb = new CleanupWrapper { Result = Removed(("tiles", 0, 0), ("temp", 0, 0)) };
        var log = new CapturingLogger<RecurringCacheCleanupService>();

        Service(ddb, new JobScheduleOptions(), log).CleanupCache(null, CancellationToken.None);

        log.Count(LogLevel.Information).ShouldBe(2);
        log.Entries[^1].Message.ShouldContain("0 entries removed");
    }

    [Test]
    public void ACanceledJob_StopsTheCleanup_AndDoesNotReportItCompleted()
    {
        var ddb = new CleanupWrapper();
        var log = new CapturingLogger<RecurringCacheCleanupService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<DdbCanceledException>(() =>
            Service(ddb, new JobScheduleOptions(), log).CleanupCache(null, cts.Token));

        ddb.Calls.ShouldHaveSingleItem().Token.IsCancellationRequested.ShouldBeTrue();
        log.Entries.ShouldNotContain(e => e.Message.Contains("completed"));
    }

    // ---- Scheduling ----

    private static async Task<InMemoryStorage> Schedule(JobScheduleOptions schedule, InMemoryStorage? storage = null)
    {
        storage ??= new InMemoryStorage();
        await new HangfireJobsInitializer(new RecurringJobManager(storage), Microsoft.Extensions.Options.Options.Create(schedule),
                Microsoft.Extensions.Options.Options.Create(new UpdateCheckOptions()), new FixedInstanceIdentity(),
                NullLogger<HangfireJobsInitializer>.Instance)
            .ExecuteAsync(CancellationToken.None);
        return storage;
    }

    private static RecurringJobDto? CleanupJob(InMemoryStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs().SingleOrDefault(j => j.Id == RecurringJobIds.CleanupCache);
    }

    [Test]
    public async Task ByDefault_TheJobRunsDailyAtHalfPastFour()
    {
        using var storage = await Schedule(new JobScheduleOptions());

        var job = CleanupJob(storage).ShouldNotBeNull();
        job.Cron.ShouldBe("30 4 * * *");
        job.Cron.ShouldBe(JobScheduleOptions.DefaultCrons.CacheCleanupCron);
        job.Job.Type.ShouldBe(typeof(RecurringCacheCleanupService));
        job.Job.Method.Name.ShouldBe(nameof(RecurringCacheCleanupService.CleanupCache));
        job.Job.Method.GetParameters().Select(p => p.ParameterType).ShouldContain(typeof(CancellationToken));
    }

    [Test]
    public async Task AnExplicitCron_IsTheSchedule()
    {
        using var storage = await Schedule(new JobScheduleOptions { CacheCleanupCron = "15 1 * * 0" });

        CleanupJob(storage).ShouldNotBeNull().Cron.ShouldBe("15 1 * * 0");
    }

    [TestCase("disabled")]
    [TestCase("off")]
    [TestCase("none")]
    public async Task ADisabledCron_RemovesTheJob(string cron)
    {
        using var storage = await Schedule(new JobScheduleOptions());
        CleanupJob(storage).ShouldNotBeNull();

        // The same storage at the next start, with the cleanup switched off
        await Schedule(new JobScheduleOptions { CacheCleanupCron = cron }, storage);

        CleanupJob(storage).ShouldBeNull();
    }

    [Test]
    public void TheJob_IsOneOfTheRecurringJobsTheApplicationOwns()
    {
        RecurringJobIds.CleanupCache.ShouldBe("cleanup-cache");
        RecurringJobIds.Owned.ShouldContain(RecurringJobIds.CleanupCache);
    }
}
