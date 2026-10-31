using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
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
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The work folder of a heavy task has one owner. It is created only once the task can run,
/// kept (with its TTL delete) only when the tool produced an artifact, and deleted on every other
/// outcome; the failure is recorded even when the job's token is already cancelled (the write that
/// records it used that token and its failure was swallowed).
/// </summary>
[TestFixture]
[NonParallelizable]
public class HeavyTaskLifecycleTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string TaskId = "4242";

    private string _temp = null!;
    private TestDb<RegistryContext> _db = null!;
    private Mock<IJobIndexWriter> _writer = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private readonly List<string> _errors = [];
    private ServiceProvider _provider = null!;

    private string WorkDir => TaskJobHelpers.ArtifactDirectory(TaskId, _temp)!;

    [SetUp]
    public void SetUp()
    {
        _temp = Path.Combine(Path.GetTempPath(), "heavytasklifecycle-heavy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _errors.Clear();
        _db = TestDb.Create<RegistryContext>();
        using (var context = _db.NewContext())
        {
            var org = new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow };
            org.Datasets = [new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }];
            context.Organizations.Add(org);
            context.SaveChanges();
        }

        _writer = new Mock<IJobIndexWriter>();
        // Like EF: a cancelled token fails the write
        _writer.Setup(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, error, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _errors.Add(error);
                return Task.CompletedTask;
            });
        _jobs = new Mock<IBackgroundJobsProcessor>();

        var ddb = new Mock<IDDB>();
        ddb.SetupGet(x => x.DatasetFolderPath).Returns(Path.Combine(_temp, "ds"));
        var manager = new Mock<IDdbManager>();
        manager.Setup(m => m.Get(Org, It.IsAny<Guid>())).Returns(ddb.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton(manager.Object);
        services.AddSingleton(_writer.Object);
        services.AddSingleton(_jobs.Object);
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
            Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings { ArtifactTtlHours = 6 }),
            new TaskWorkDirectories(TestScratch.For(_temp), new FileSystem(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskWorkDirectories>.Instance),
            new FileJobs(new FileSystem(), Mock.Of<ILogger<FileJobs>>()),
            Mock.Of<ILogger<HeavyTaskJobWrapper>>());
    }

    private static PerformContext Context() =>
        new(null, Mock.Of<IStorageConnection>(),
            new BackgroundJob(TaskId, Job.FromExpression<HealthCheckJob>(j => j.Ping(null)), DateTime.UtcNow),
            Mock.Of<IJobCancellationToken>());

    private static string Request(string ds = Ds) =>
        JsonSerializer.Serialize(new HeavyToolRequest("t", "1", Org, ds, null, JsonDocument.Parse("{}").RootElement));

    /// <summary>A tool producing an artifact (or not) in its work folder, or failing.</summary>
    private sealed class Tool(Func<IHeavyToolExecutionContext, CancellationToken, HeavyToolArtifact?> execute) : IHeavyTool
    {
        public string Id => "t";
        public string Version => "1";
        public string Title => "test";
        public HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
        public bool ProducesArtifact => true;
        public JsonDocument InputSchema { get; } = JsonDocument.Parse("{}");
        public string? SeenWorkDir { get; private set; }

        public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct) =>
            Task.CompletedTask;

        public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx) => new(null, "t", null, null);

        public Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, IHeavyToolExecutionContext ctx,
            IProgress<HeavyToolProgress> progress, CancellationToken ct)
        {
            SeenWorkDir = ctx.WorkDir;
            Directory.Exists(ctx.WorkDir).ShouldBeTrue("the tool gets its work folder");
            return Task.FromResult(execute(ctx, ct));
        }
    }

    [Test]
    public async Task DatasetGone_LeavesNoWorkFolder()
    {
        var tool = new Tool((_, _) => null);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Wrapper(tool).Run("t", "1", Request("deleted"), Context(), CancellationToken.None));

        Directory.Exists(WorkDir).ShouldBeFalse();
        tool.SeenWorkDir.ShouldBeNull();
    }

    [Test]
    public async Task ToolWithoutArtifact_DeletesItsWorkFolder_AndSchedulesNothing()
    {
        var tool = new Tool((ctx, _) =>
        {
            File.WriteAllText(Path.Combine(ctx.WorkDir!, "partial.tmp"), "x");
            return null;
        });

        await Wrapper(tool).Run("t", "1", Request(), Context(), CancellationToken.None);

        Directory.Exists(WorkDir).ShouldBeFalse();
        _jobs.Verify(j => j.Schedule(It.IsAny<Expression<Action<FileJobs>>>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Test]
    public async Task Artifact_KeepsTheWorkFolder_WithItsTtlDelete()
    {
        var tool = new Tool((ctx, _) =>
        {
            File.WriteAllText(Path.Combine(ctx.WorkDir!, "out.zip"), "zip");
            return new HeavyToolArtifact("out.zip", "application/zip", "out.zip", 3, "abc");
        });

        await Wrapper(tool).Run("t", "1", Request(), Context(), CancellationToken.None);

        Directory.Exists(WorkDir).ShouldBeTrue();
        _jobs.Verify(j => j.Schedule(It.IsAny<Expression<Action<FileJobs>>>(), TimeSpan.FromHours(6)), Times.Once);
        _writer.Verify(w => w.UpdateArtifactAsync(TaskId, 3, "abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task FailureAfterTheTokenWasCancelled_IsStillRecorded_AndTheFolderDeleted()
    {
        using var cts = new CancellationTokenSource();
        var tool = new Tool((_, _) =>
        {
            cts.Cancel(); // the server shuts down while the tool fails for its own reason
            throw new InvalidDataException("corrupt input");
        });

        await Should.ThrowAsync<InvalidDataException>(() =>
            Wrapper(tool).Run("t", "1", Request(), Context(), cts.Token));

        _errors.ShouldBe([nameof(InvalidDataException)]);
        Directory.Exists(WorkDir).ShouldBeFalse();
    }

    [Test]
    public async Task Cancellation_DeletesTheWorkFolder_AndRethrows()
    {
        using var cts = new CancellationTokenSource();
        var tool = new Tool((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return null;
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Wrapper(tool).Run("t", "1", Request(), Context(), cts.Token));

        Directory.Exists(WorkDir).ShouldBeFalse();
        _errors.ShouldBeEmpty("a cancellation is not a failure of the tool");
    }
}
