using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// When the worker rolls a task back (FR-XC-27): when the user cancelled it (its Hangfire job is Deleted) or it failed
/// for good; never when the server stops (the same token fires) nor before an automatic retry, where the records still
/// buffered are written instead. At its start an attempt applies the attempt rule; a task that ends well commits.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HeavyTaskRollbackTriggerTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string TaskId = "4343";

    private string _temp = null!;
    private TestDb<RegistryContext> _db = null!;
    private Guid _internalRef;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private Mock<ITaskRollback> _rollback = null!;
    private Mock<ITaskWriteRecordStore> _store = null!;
    private readonly List<string> _errors = [];
    private readonly List<string?> _messages = [];
    private readonly List<string> _flushed = [];
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _temp = Path.Combine(Path.GetTempPath(), "heavytaskrollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _errors.Clear();
        _messages.Clear();
        _flushed.Clear();
        _db = TestDb.Create<RegistryContext>();
        _internalRef = Guid.NewGuid();
        using (var context = _db.NewContext())
        {
            var org = new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow };
            org.Datasets = [new Dataset { Slug = Ds, InternalRef = _internalRef, CreationDate = DateTime.UtcNow }];
            context.Organizations.Add(org);
            context.SaveChanges();
        }

        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, string?, CancellationToken>((_, error, message, _) =>
            {
                _errors.Add(error);
                _messages.Add(message);
                return Task.CompletedTask;
            });
        _jobs = new Mock<IBackgroundJobsProcessor>();
        _jobs.Setup(j => j.GetJobStatus(TaskId)).Returns(JobStatus.Processing);
        _rollback = new Mock<ITaskRollback>();
        _rollback.Setup(r => r.StartAttemptAsync(TaskId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskAttemptStart.Fresh);
        _rollback.Setup(r => r.RollBackAsync(TaskId, It.IsAny<IReadOnlyCollection<PendingWrite>?>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(TaskRollbackOutcome.RolledBack);
        _store = new Mock<ITaskWriteRecordStore>();
        _store.Setup(s => s.RecordAsync(TaskId, It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, DatasetKey, IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken>(
                (_, _, paths, _, _) => _flushed.AddRange(paths))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.MarkCommittedAsync(TaskId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var ddb = new Mock<IDDB>();
        ddb.SetupGet(x => x.DatasetFolderPath).Returns(Path.Combine(_temp, "ds"));
        var manager = new Mock<IDdbManager>();
        manager.Setup(m => m.Get(Org, It.IsAny<Guid>())).Returns(ddb.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton(manager.Object);
        services.AddSingleton(writer.Object);
        services.AddSingleton(_jobs.Object);
        services.AddSingleton(_rollback.Object);
        services.AddSingleton(_store.Object);
        services.AddScoped(_ => TestJobs.DdbJobs(manager.Object));
        services.AddScoped<Registry.Web.Services.Builds.BuildScheduler>(_ => TestJobs.Scheduler(_jobs.Object));
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _db.Dispose();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }

    private HeavyTaskJobWrapper Wrapper(IHeavyTool tool)
    {
        var registry = new Mock<IHeavyToolRegistry>();
        registry.Setup(r => r.Resolve(tool.Id, tool.Version)).Returns(tool);
        return new HeavyTaskJobWrapper(_provider.GetRequiredService<IServiceScopeFactory>(), registry.Object,
            Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings()),
            new TaskWorkDirectories(TestScratch.For(_temp), new FileSystem(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskWorkDirectories>.Instance),
            new FileJobs(new FileSystem(), Mock.Of<ILogger<FileJobs>>()),
            Mock.Of<ILogger<HeavyTaskJobWrapper>>());
    }

    private static PerformContext Context(int retryCount = 0)
    {
        var connection = new Mock<IStorageConnection>();
        connection.Setup(c => c.GetJobParameter(TaskId, "RetryCount")).Returns(retryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new(null, connection.Object,
            new BackgroundJob(TaskId, Job.FromExpression<HealthCheckJob>(j => j.Ping(null)), DateTime.UtcNow),
            Mock.Of<IJobCancellationToken>());
    }

    private static string Request() =>
        JsonSerializer.Serialize(new HeavyToolRequest("w", "1", Org, Ds, null, JsonDocument.Parse("{}").RootElement));

    /// <summary>A tool that adds dataset files: it records one, then does what the test says.</summary>
    private sealed class WritingTool(Action<CancellationToken> afterRecord, bool addsDatasetFiles = true) : IHeavyTool
    {
        public string Id => "w";
        public string Version => "1";
        public string Title => "writer";
        public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
        public bool ProducesArtifact => false;
        public bool AddsDatasetFiles => addsDatasetFiles;
        public JsonDocument InputSchema { get; } = JsonDocument.Parse("{}");
        public bool Ran { get; private set; }
        public ITaskWriteRecorder? Recorder { get; private set; }

        public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct) =>
            Task.CompletedTask;

        public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx) => new(null, "w", null, null);

        public Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, IHeavyToolExecutionContext ctx,
            IProgress<HeavyToolProgress> progress, CancellationToken ct)
        {
            Ran = true;
            Recorder = ctx.WriteRecord;
            if (addsDatasetFiles)
                ctx.WriteRecord.Record("out/a.tif");
            afterRecord(ct);
            return Task.FromResult<HeavyToolArtifact?>(null);
        }
    }

    private void VerifyRolledBackWithThePendingPath() =>
        _rollback.Verify(r => r.RollBackAsync(TaskId,
            It.Is<IReadOnlyCollection<PendingWrite>?>(p => p != null && p.Single().Path == "out/a.tif" &&
                                                           p.Single().Dataset == new DatasetKey(Org, _internalRef)),
            It.IsAny<CancellationToken>()), Times.Once);

    private void VerifyNotRolledBack() =>
        _rollback.Verify(r => r.RollBackAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<PendingWrite>?>(),
            It.IsAny<CancellationToken>()), Times.Never);

    [Test]
    public async Task AServerStop_DoesNotRollBack_AndWritesThePendingRecords()
    {
        using var cts = new CancellationTokenSource();
        var tool = new WritingTool(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(), cts.Token));

        VerifyNotRolledBack();
        _flushed.ShouldBe(["out/a.tif"]);
    }

    [Test]
    public async Task AServerStopDuringAFailure_DoesNotRollBack()
    {
        using var cts = new CancellationTokenSource();
        var tool = new WritingTool(_ =>
        {
            cts.Cancel();
            throw new InvalidOperationException("the native call was interrupted");
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(), cts.Token));

        VerifyNotRolledBack();
        _flushed.ShouldBe(["out/a.tif"]);
    }

    [Test]
    public async Task AUserCancel_RollsBack_FromTheDeletedJobState()
    {
        using var cts = new CancellationTokenSource();
        var tool = new WritingTool(ct =>
        {
            _jobs.Setup(j => j.GetJobStatus(TaskId)).Returns(JobStatus.Deleted);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(), cts.Token));

        VerifyRolledBackWithThePendingPath();
    }

    [Test]
    public async Task AFinalFailure_RollsBack()
    {
        var tool = new WritingTool(_ => throw new InvalidOperationException("broken archive"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(), CancellationToken.None));

        VerifyRolledBackWithThePendingPath();
        _errors.ShouldBe([nameof(InvalidOperationException)]);
    }

    [Test]
    public async Task AFailureThatIsRetried_DoesNotRollBack_AndKeepsTheRecord()
    {
        var tool = new WritingTool(_ => throw new DdbBusyException("The index is busy"));

        await Should.ThrowAsync<DdbBusyException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(retryCount: 0), CancellationToken.None));

        VerifyNotRolledBack();
        _flushed.ShouldBe(["out/a.tif"]);
    }

    [Test]
    public async Task TheLastAttemptOfARetriedFailure_RollsBack()
    {
        var tool = new WritingTool(_ => throw new DdbBusyException("The index is busy"));

        await Should.ThrowAsync<DdbBusyException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(retryCount: DdbRetryPolicy.Attempts), CancellationToken.None));

        VerifyRolledBackWithThePendingPath();
    }

    [Test]
    public async Task ASuccess_WritesTheRecords_AndCommits()
    {
        var tool = new WritingTool(_ => { });

        await Wrapper(tool).Run("w", "1", Request(), Context(retryCount: 2), CancellationToken.None);

        _flushed.ShouldBe(["out/a.tif"]);
        _store.Verify(s => s.MarkCommittedAsync(TaskId, It.IsAny<CancellationToken>()), Times.Once);
        _rollback.Verify(r => r.StartAttemptAsync(TaskId, 2, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNotRolledBack();
    }

    [Test]
    public async Task ATaskLostAfterItCommitted_DoesNotRunAgain_AndSaysWhy()
    {
        _rollback.Setup(r => r.StartAttemptAsync(TaskId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskAttemptStart.CommittedLost);
        var tool = new WritingTool(_ => { });

        await Should.ThrowAsync<TaskCommittedLostException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(), CancellationToken.None));

        tool.Ran.ShouldBeFalse();
        _errors.ShouldBe([nameof(TaskCommittedLostException)]);
        _messages.Single().ShouldBe(new TaskCommittedLostException().Message);
    }

    [Test]
    public async Task AToolThatAddsNoFiles_HasNoRecord()
    {
        var tool = new WritingTool(_ => throw new InvalidOperationException("failed"), addsDatasetFiles: false);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Wrapper(tool).Run("w", "1", Request(), Context(), CancellationToken.None));

        tool.Recorder!.Enabled.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => tool.Recorder.Record("x.tif"));
        VerifyNotRolledBack();
        _rollback.Verify(r => r.StartAttemptAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ATaskThatCannotStart_RollsBackWhatWasCreatedForIt()
    {
        var registry = new Mock<IHeavyToolRegistry>();
        var wrapper = new HeavyTaskJobWrapper(_provider.GetRequiredService<IServiceScopeFactory>(), registry.Object,
            Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings()),
            new TaskWorkDirectories(TestScratch.For(_temp), new FileSystem(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskWorkDirectories>.Instance),
            new FileJobs(new FileSystem(), Mock.Of<ILogger<FileJobs>>()), Mock.Of<ILogger<HeavyTaskJobWrapper>>());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            wrapper.Run("gone", "1", Request(), Context(), CancellationToken.None));

        _rollback.Verify(r => r.RollBackAsync(TaskId, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
