using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="ImportDatasetTool"/> over a fake source (FR-XC-26, FR-XC-27, D-039): it adds dataset files, hands the
/// source the write record of its task, indexes what the import created and never the other files of the folder, stops
/// at the storage budget with a quota error, and leaves the rollback (cancel, failure, quota stop) to the task: it
/// deletes nothing itself.
/// </summary>
[TestFixture]
public class ImportDatasetToolTests
{
    private string _dataset = null!;
    private FakeTaskWriteRecorder _recorder = null!;
    private Mock<IDDB> _ddb = null!;
    private IHeavyToolExecutionContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _dataset = Path.Combine(Path.GetTempPath(), "import-dataset-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataset);
        _recorder = new FakeTaskWriteRecorder();
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dataset);
        _ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> paths) => [.. paths.Select(p => new Entry { Path = p, Type = EntryType.Generic })]);
        TestBuilds.SetupBuilds(_ddb);

        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(_ddb.Object);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("org", Guid.NewGuid()));
        ctx.SetupGet(c => c.WriteRecord).Returns(_recorder);
        ctx.SetupGet(c => c.BuildQueue).Returns(TestJobs.Scheduler());
        _ctx = ctx.Object;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataset))
            Directory.Delete(_dataset, true);
    }

    private static ImportDatasetTool ToolFor(IImportSource source)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new ImportSettings());
        return new ImportDatasetTool(
            new ImportSourceCatalog(new ImportSourceFactory([source]), settings),
            new ImportCredentials(Mock.Of<IImportCredentialProtector>()),
            TestJobs.FileIndexer(), settings,
            Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings { DiskSafetyMarginBytes = 0 }),
            TestHeadroom.WithFree(1L << 40, floorMb: 0));
    }

    private static HeavyToolRequest Request(long? budgetBytes = null) => new("import-dataset", "1", "org", "ds", null,
        JsonSerializer.SerializeToElement(new { sourceType = "registry", budgetBytes, @params = new { } }));

    private Task Execute(ImportDatasetTool tool, CancellationToken ct = default) =>
        tool.ExecuteAsync(Request(), _ctx, new Progress<HeavyToolProgress>(), ct);

    [Test]
    public void AddsDatasetFiles() => ToolFor(new ScriptedSource(_ => Task.CompletedTask)).AddsDatasetFiles.ShouldBeTrue();

    [Test]
    public async Task IndexesTheFilesTheImportCreated_NeverTheOtherFilesOfTheFolder()
    {
        File.WriteAllText(Path.Combine(_dataset, "other.jpg"), "somebody else's");
        var source = new ScriptedSource(async s =>
        {
            s.Writes.Record("a.jpg");
            await File.WriteAllTextAsync(Path.Combine(_dataset, "a.jpg"), "a");
            s.Writes.Written("a.jpg");
        });

        await Execute(ToolFor(source));

        _ddb.Verify(d => d.AddRawBatch(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "a.jpg" }))), Times.Once);
        _recorder.Paths.ShouldBe(["a.jpg"]);
    }

    [Test]
    public async Task APathAnEarlierAttemptRecorded_IsIndexedToo_WhenItsFileIsThere()
    {
        File.WriteAllText(Path.Combine(_dataset, "earlier.jpg"), "e");
        _recorder.Earlier.Add("earlier.jpg");
        _recorder.Earlier.Add("gone.jpg");
        var source = new ScriptedSource(async s =>
        {
            s.Writes.Record("b.jpg");
            await File.WriteAllTextAsync(Path.Combine(_dataset, "b.jpg"), "b");
        });

        await Execute(ToolFor(source));

        _ddb.Verify(d => d.AddRawBatch(It.Is<IReadOnlyList<string>>(p =>
            p.SequenceEqual(new[] { "b.jpg", "earlier.jpg" }))), Times.Once);
    }

    [Test]
    public async Task TheBudgetBreach_StopsTheTransfer_AsAQuotaError_AndDeletesNothingItself()
    {
        var source = new ScriptedSource(async s =>
        {
            s.Writes.Record("a.jpg");
            await File.WriteAllTextAsync(Path.Combine(_dataset, "a.jpg"), "aaaa");
            s.Progress.Report(new ImportProgress(0.5, "downloading", BytesSoFar: 5000));

            // The source stops with the cancellation it is given (the client rethrows it, FR-XC-27)
            s.Ct.ThrowIfCancellationRequested();
            Assert.Fail("the transfer was not stopped");
        });

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            ToolFor(source).ExecuteAsync(Request(budgetBytes: 1000), _ctx, new Progress<HeavyToolProgress>(),
                CancellationToken.None));

        ex.Message.ShouldContain("storage budget");
        File.Exists(Path.Combine(_dataset, "a.jpg")).ShouldBeTrue("the task rolls the import back from its record");
        _recorder.Paths.ShouldBe(["a.jpg"]);
        _ddb.Verify(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Test]
    public async Task AUserCancel_IsACancellation_NotAQuotaError()
    {
        using var cts = new CancellationTokenSource();
        var source = new ScriptedSource(s =>
        {
            cts.Cancel();
            s.Ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Execute(ToolFor(source), cts.Token));

        _ddb.Verify(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    private sealed record Call(IImportWriteRecord Writes, IProgress<ImportProgress> Progress, CancellationToken Ct);

    private sealed class ScriptedSource(Func<Call, Task> script) : IImportSource
    {
        public string SourceType => "registry";

        public Task<ImportSourceProbe> ProbeAsync(JsonElement parameters, CancellationToken ct) =>
            Task.FromResult(new ImportSourceProbe(true, null, null, null, null));

        public Task FetchAsync(JsonElement parameters, string destFolder, IImportWriteRecord writes,
            IProgress<ImportProgress> progress, CancellationToken ct) => script(new Call(writes, progress, ct));
    }
}
