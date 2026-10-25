#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Jobs;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The shared job infrastructure. One console writes each line to the server log as a
/// structured record and to the job console/tail rendered and scrubbed; native records of a job's
/// calls carry its id and dataset and reach its console through the job's own thread; the
/// dataset sweep keeps going past a failed dataset and stops on cancellation; the Hangfire filters
/// are DI-built and installed once however many hosts are built; recurring jobs take a token and
/// cannot overlap across hosts.
/// </summary>
[TestFixture]
[NonParallelizable]
public class JobInfrastructureTests
{
    // ---- MessageTemplate ----

    [Test]
    public void MessageTemplate_RendersPositionalHolesWithFormatsAndEscapes()
    {
        MessageTemplate.Render("Removed {Count} entries from {Dataset}", [3, "o/d"]).ShouldBe("Removed 3 entries from o/d");
        MessageTemplate.Render("{When:yyyy-MM-dd} {Ratio:0.0} {{literal}}",
            [new DateTime(2026, 10, 22), 0.25]).ShouldBe("2026-10-22 0.3 {literal}");
        MessageTemplate.Render("{@Obj} {$Str} {Pad,5}|", ["a", "b", "x"]).ShouldBe("a b     x|");
        MessageTemplate.Render("{A} {B}", ["only-a"]).ShouldBe("only-a {B}");
        MessageTemplate.Render("{A}", [null]).ShouldBe("(null)");
        MessageTemplate.HoleNames("{@A} {B:0} {C,3} {{D}}").ShouldBe(["A", "B", "C"]);
    }

    // ---- JobConsole ----

    private sealed class StructuredLogger : ILogger
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Ex)>
            Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add((logLevel, formatter(state, exception),
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? [], exception));
    }

    private static (PerformContext Context, LogRingBuffer Tail) JobContext(string jobId = "7")
    {
        var job = new BackgroundJob(jobId, Job.FromExpression<HealthCheckJob>(j => j.Ping(null)), DateTime.UtcNow);
        var context = new PerformContext(null, Mock.Of<IStorageConnection>(), job, Mock.Of<IJobCancellationToken>());
        var tail = new LogRingBuffer();
        context.Items[BuildLogCaptureFilter.BufferKey] = tail;
        return (context, tail);
    }

    [Test]
    public void JobConsole_LogsAStructuredRecord_AndAScrubbedRenderedLine()
    {
        var logger = new StructuredLogger();
        var (context, tail) = JobContext();
        var console = JobConsole.For(logger, context, "/data/datasets/o/abc");

        console.Info("Cleaned {Dataset}: {Entries} entries at /data/datasets/o/abc/x", "o/d", 4);
        var ex = new InvalidOperationException("boom");
        console.Warning(ex, "Cleanup failed for {Dataset}: {Error}", "o/d", ex.Message);

        var (level, message, state, _) = logger.Records[0];
        level.ShouldBe(LogLevel.Information);
        message.ShouldBe("Cleaned o/d: 4 entries at /data/datasets/o/abc/x"); // the server log is not public
        state.ShouldContain(new KeyValuePair<string, object?>("Dataset", "o/d"));
        state.ShouldContain(new KeyValuePair<string, object?>("Entries", 4));
        state.ShouldContain(new KeyValuePair<string, object?>("{OriginalFormat}",
            "Cleaned {Dataset}: {Entries} entries at /data/datasets/o/abc/x"));
        logger.Records[1].Level.ShouldBe(LogLevel.Warning);
        logger.Records[1].Ex.ShouldBeSameAs(ex);

        tail.Snapshot().Lines.Select(l => l.Msg).ShouldBe([
            "Cleaned o/d: 4 entries at [dataset]/x",
            "WARNING: Cleanup failed for o/d: boom"
        ]);
    }

    [Test]
    public void JobConsole_WithoutAJob_WritesTheServerLogOnly()
    {
        var logger = new StructuredLogger();

        JobConsole.For(logger, null).Debug("Running {Step}", "build");

        logger.Records.Single().Level.ShouldBe(LogLevel.Debug);
        logger.Records.Single().Message.ShouldBe("Running build");
    }

    // ---- native records of a job ----

    [Test]
    public void NativeRecordOfAJob_CarriesJobAndDataset_AndReachesTheConsoleThroughTheJobsThread()
    {
        var factory = new StructuredLoggerFactory();
        var sink = new DdbLogSink(factory);
        var (context, tail) = JobContext("job-42");

        using (var operation = JobOperations.Begin("job-42", "org/ds"))
        {
            DdbOperation.Current.ShouldBe(operation.Id);
            JobOperations.Current.ShouldBeSameAs(operation);

            // As libddb's callback calls it, from a native thread
            var native = new Thread(() => sink.Write(DdbLogLevel.Warning, "GDAL: cannot open /data/datasets/o/abc/a.tif",
                "gdal", operation.Id));
            native.Start();
            native.Join();
            sink.Write(DdbLogLevel.Debug, "detail", "ddb", operation.Id);

            var record = factory.Logger.Records.First();
            record.State.ShouldContain(new KeyValuePair<string, object?>("JobId", "job-42"));
            record.State.ShouldContain(new KeyValuePair<string, object?>("Dataset", "org/ds"));

            // Nothing reached the console from the native thread; the job's next line writes it first
            tail.Snapshot().Lines.ShouldBeEmpty();
            JobConsole.For(new StructuredLogger(), context, "/data/datasets/o/abc").Info("Done build");
            tail.Snapshot().Lines.Select(l => l.Msg).ShouldBe([
                "[gdal] GDAL: cannot open [dataset]/a.tif", // warnings only, scrubbed
                "Done build"
            ]);
        }

        DdbOperation.Current.ShouldBe(0UL);
    }

    [Test]
    public void NativeLinesOfAJob_AreBounded()
    {
        using var operation = JobOperations.Begin("j", null);
        for (var i = 0; i < JobOperation.MaxQueuedLines + 5; i++)
            operation.QueueNativeLine("line " + i);

        var lines = operation.TakeNativeLines().ToArray();
        lines.Length.ShouldBe(JobOperation.MaxQueuedLines + 1);
        lines[^1].ShouldBe("(5 more native messages in the server log)");
    }

    [Test]
    public void JobOperationFilter_GivesEachJobItsOperationForAsLongAsItRuns()
    {
        var connection = new Mock<IStorageConnection>();
        connection.Setup(x => x.GetJobParameter("9", JobParamKeys.OrgSlug)).Returns("org");
        connection.Setup(x => x.GetJobParameter("9", JobParamKeys.DsSlug)).Returns("ds");
        var job = new BackgroundJob("9", Job.FromExpression<HealthCheckJob>(j => j.Ping(null)), DateTime.UtcNow);
        var perform = new PerformContext(null, connection.Object, job, Mock.Of<IJobCancellationToken>());
        var filter = new JobOperationFilter(Mock.Of<ILogger<JobOperationFilter>>());

        filter.OnPerforming(new PerformingContext(perform));
        var operation = JobOperations.Current.ShouldNotBeNull();
        operation.JobId.ShouldBe("9");
        operation.Dataset.ShouldBe("org/ds");

        filter.OnPerformed(new PerformedContext(perform, null, false, null));
        JobOperations.Current.ShouldBeNull();
        JobOperations.Find(operation.Id).ShouldBeNull();
    }

    [Test]
    public void JobOperationFilter_TheJobsCancellation_IsItsNativeCallsCancellation()
    {
        // A build (or any cancellable native call of a job) stops when Hangfire cancels the job
        using var shutdown = new CancellationTokenSource();
        var token = new Mock<IJobCancellationToken>();
        token.SetupGet(t => t.ShutdownToken).Returns(shutdown.Token);
        var job = new BackgroundJob("10", Job.FromExpression<HealthCheckJob>(j => j.Ping(null)), DateTime.UtcNow);
        var perform = new PerformContext(null, Mock.Of<IStorageConnection>(), job, token.Object);
        var filter = new JobOperationFilter(Mock.Of<ILogger<JobOperationFilter>>());

        filter.OnPerforming(new PerformingContext(perform));
        DdbCancellation.Current.ShouldBe(shutdown.Token);

        filter.OnPerformed(new PerformedContext(perform, null, false, null));
        DdbCancellation.Current.CanBeCanceled.ShouldBeFalse("the job's cancellation ends with the job");
    }

    private sealed class StructuredLoggerFactory : ILoggerFactory
    {
        public StructuredLogger Logger { get; } = new();
        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    // ---- DatasetSweep ----

    [Test]
    public async Task DatasetSweep_CountsAFailedDatasetAndGoesOn_AndStopsOnCancellation()
    {
        await using var context = TestDb.CreateContext<RegistryContext>();
        var org = new Organization { Slug = "o", Name = "o", CreationDate = DateTime.UtcNow };
        org.Datasets =
        [
            new Dataset { Slug = "a", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow },
            new Dataset { Slug = "b", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow },
            new Dataset { Slug = "c", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }
        ];
        context.Organizations.Add(org);
        await context.SaveChangesAsync();
        var logger = new StructuredLogger();
        var visited = new List<string>();

        var result = await new DatasetSweep(context).ForEachAsync(JobConsole.For(logger, null), "Cleanup", (ds, _) =>
        {
            visited.Add(ds.Key);
            return ds.DsSlug == "b" ? throw new InvalidOperationException("broken") : Task.CompletedTask;
        }, CancellationToken.None);

        visited.Order().ShouldBe(["o/a", "o/b", "o/c"]);
        result.ShouldBe(new DatasetSweepResult(3, 1));
        logger.Records.ShouldContain(r => r.Level == LogLevel.Warning && r.Message == "Cleanup failed for o/b: broken");

        using var cts = new CancellationTokenSource();
        var after = 0;
        await Should.ThrowAsync<OperationCanceledException>(() => new DatasetSweep(context).ForEachAsync(
            JobConsole.For(logger, null), "Cleanup", (_, _) =>
            {
                after++;
                cts.Cancel();
                return Task.CompletedTask;
            }, cts.Token));
        after.ShouldBe(1, "the sweep stops before the next dataset");
    }

    // ---- Filters: DI-built, installed once ----

    [Test]
    public void JobFilters_AreDiBuilt_AndInstalledOnceHoweverManyHostsAreBuilt()
    {
        var saved = GlobalJobFilters.Filters.ToList();
        try
        {
            ServiceProvider Host()
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton(Mock.Of<IHeavyToolRegistry>());
                foreach (var type in RegistryJobsServiceCollectionExtensions.JobFilterTypes)
                    services.AddSingleton(type);
                return services.BuildServiceProvider();
            }

            using var first = Host();
            using var second = Host();
            RegistryJobsServiceCollectionExtensions.InstallJobFilters(first);
            RegistryJobsServiceCollectionExtensions.InstallJobFilters(second);
            RegistryJobsServiceCollectionExtensions.InstallJobFilters(second);

            foreach (var type in RegistryJobsServiceCollectionExtensions.JobFilterTypes)
            {
                var installed = GlobalJobFilters.Filters.Where(f => f.Instance.GetType() == type).ToArray();
                installed.Length.ShouldBe(1, type.Name);
                installed[0].Instance.ShouldBeSameAs(second.GetRequiredService(type), "the last host's instance");
            }
        }
        finally
        {
            foreach (var filter in GlobalJobFilters.Filters.ToList())
                GlobalJobFilters.Filters.Remove(filter.Instance);
            foreach (var filter in saved)
                GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }

    // ---- Recurring jobs ----

    private static readonly Type[] RecurringServices =
    [
        typeof(JobIndexSyncService), typeof(BuildPendingService), typeof(OrphanedDatasetCleanupService),
        typeof(JobIndexCleanupService), typeof(RecurringDatasetCleanupService),
        typeof(ArtifactCompletenessCheckerService), typeof(IndexReconciliationService)
    ];

    [Test]
    public void RecurringJobs_ThatCanOverlapAcrossHosts_RunOneAtATime()
    {
        // The recovery pass is built to overlap (idempotent, bounded) and the expired-jobs sweep is a
        // short idempotent daily run; every other sweep holds the lock of its job with timeout 0
        foreach (var type in RecurringServices)
        {
            var attribute = type.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), true)
                .Cast<DisableConcurrentExecutionAttribute>().SingleOrDefault();
            attribute.ShouldNotBeNull(type.Name);
        }
    }

    [Test]
    public async Task RecurringDefinitions_ScheduleTheOverloadsWithACancellationToken()
    {
        using var storage = new InMemoryStorage();
        await new HangfireJobsInitializer(new RecurringJobManager(storage),
                Microsoft.Extensions.Options.Options.Create(new Models.Configuration.JobScheduleOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HangfireJobsInitializer>.Instance)
            .ExecuteAsync(CancellationToken.None);

        using var connection = storage.GetConnection();
        var jobs = connection.GetRecurringJobs();
        jobs.Count.ShouldBe(RecurringJobIds.Owned.Count);
        foreach (var job in jobs)
            job.Job.Method.GetParameters().Select(p => p.ParameterType)
                .ShouldContain(typeof(CancellationToken), job.Id);
    }
}
