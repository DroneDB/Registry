#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-10, the archive extraction of <see cref="ArchiveExtractTool"/> (one loop for the three
/// extracting features since RQ-10): the dataset path rule decides every entry (an unsafe name fails
/// the extraction, a reserved one is skipped), and a failed extraction leaves the dataset as it was:
/// every file written is removed, every file replaced is put back, every folder created is removed.
/// Before RQ-10 only a quota error rolled back, and that rollback deleted the files it had replaced.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq10ArchiveExtractionTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq10ArchiveExtractionTests
{
    private string _dataset = null!;
    private readonly HashSet<string> _indexed = new(StringComparer.Ordinal);
    private readonly List<string> _added = [];

    [SetUp]
    public void SetUp()
    {
        _dataset = Path.Combine(Path.GetTempPath(), "rq10-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataset);
        _indexed.Clear();
        _added.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataset))
            Directory.Delete(_dataset, true);
    }

    [Test]
    public async Task Execute_SkipsReservedEntries_AndExtractsTheRest()
    {
        Zip(("a.txt", "a"), ("sub/b.txt", "b"), (".ddb/dbase.sqlite", "index"), (".uploads/y.tmp", "staged"),
            ("sub/.ddb/z", "nested"));

        await ExecuteAsync(new { sourcePath = "archive.zip" });

        Files().ShouldBe(["a.txt", "archive.zip", "sub/b.txt"]);
        _added.ShouldBe(["a.txt", "sub/b.txt"]);
    }

    [Test]
    public async Task Execute_Cancelled_RemovesEveryFileItWrote()
    {
        Zip(("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ExecuteAsync(new { sourcePath = "archive.zip" }, p =>
            {
                if (p.Message == "a.txt") cts.Cancel();
            }, ct: cts.Token));

        Files().ShouldBe(["archive.zip"]);
        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task Execute_ZipSlipEntryHalfway_FailsAndRemovesWhatItWrote()
    {
        Zip(("newdir/a.txt", "a"), ("../evil.txt", "evil"));

        await Should.ThrowAsync<Exception>(() => ExecuteAsync(new { sourcePath = "archive.zip" }));

        Files().ShouldBe(["archive.zip"]);
        Directory.Exists(Path.Combine(_dataset, "newdir")).ShouldBeFalse("a folder the extraction created is removed");
        File.Exists(Path.Combine(Path.GetDirectoryName(_dataset)!, "evil.txt")).ShouldBeFalse();
        _added.ShouldBeEmpty();
    }

    // A drive is absolute on every OS (libddb's rule); on Linux this entry became "C_/evil.txt"
    [TestCase(@"C:\evil.txt")]
    [TestCase("C:/evil.txt")]
    public async Task Execute_DriveEntry_FailsTheExtraction(string entry)
    {
        Zip(("a.txt", "a"), (entry, "evil"));

        await Should.ThrowAsync<Exception>(() => ExecuteAsync(new { sourcePath = "archive.zip" }));

        Files().ShouldBe(["archive.zip"]);
    }

    [Test]
    public async Task Execute_OverwriteThenOutOfBudget_PutsTheReplacedFileBack()
    {
        Existing("a.txt", "original");
        Zip(("a.txt", "replacement"), ("big.bin", new string('x', 2 * 1024 * 1024)));

        await Should.ThrowAsync<QuotaExceededException>(() =>
            ExecuteAsync(new { sourcePath = "archive.zip", overwrite = true }, maxArchiveBytes: 1024 * 1024));

        File.ReadAllText(Path.Combine(_dataset, "a.txt")).ShouldBe("original");
        Files().ShouldBe(["a.txt", "archive.zip"]);
    }

    [Test]
    public async Task Execute_Overwrite_ReplacesTheFile_AndKeepsNoBackup()
    {
        Existing("a.txt", "original");
        Zip(("a.txt", "replacement"), ("b.txt", "b"));

        await ExecuteAsync(new { sourcePath = "archive.zip", overwrite = true });

        File.ReadAllText(Path.Combine(_dataset, "a.txt")).ShouldBe("replacement");
        Files().ShouldBe(["a.txt", "archive.zip", "b.txt"]);
    }

    [Test]
    public async Task Execute_WithoutOverwrite_KeepsAnIndexedFile()
    {
        Existing("a.txt", "original");
        Zip(("a.txt", "replacement"), ("b.txt", "b"));

        await ExecuteAsync(new { sourcePath = "archive.zip", destPath = "" });

        File.ReadAllText(Path.Combine(_dataset, "a.txt")).ShouldBe("original");
        _added.ShouldBe(["b.txt"]);
    }

    [Test]
    public async Task Execute_IntoAFolder_JoinsAndNormalizesTheNames()
    {
        Zip((@"x\y.txt", "y"), ("./z//w.txt", "w"));

        await ExecuteAsync(new { sourcePath = "archive.zip", destPath = "dest/" });

        Files().ShouldBe(["archive.zip", "dest/x/y.txt", "dest/z/w.txt"]);
        _added.ShouldBe(["dest/x/y.txt", "dest/z/w.txt"]);
    }

    // ---- harness

    private void Existing(string path, string content)
    {
        File.WriteAllText(Path.Combine(_dataset, path), content);
        _indexed.Add(path);
    }

    private void Zip(params (string Name, string Content)[] entries)
    {
        using var file = File.Create(Path.Combine(_dataset, "archive.zip"));
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    private string[] Files() => [.. Directory.EnumerateFiles(_dataset, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(_dataset, f).Replace('\\', '/'))
        .Order(StringComparer.Ordinal)];

    private async Task ExecuteAsync(object parameters, Action<HeavyToolProgress>? onProgress = null,
        long maxArchiveBytes = 5L * 1024 * 1024 * 1024, CancellationToken ct = default)
    {
        var tool = Rq10ExtractionHarness.CreateTool(maxArchiveBytes, Scope());
        await tool.ExecuteAsync(
            new HeavyToolRequest("archive-extract", "1", "org", "ds", null, JsonSerializer.SerializeToElement(parameters)),
            Ctx(), new SyncProgress(onProgress ?? (_ => { })), ct);
    }

    private IHeavyToolExecutionContext Ctx()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dataset);
        Support.TestBuilds.SetupBuilds(ddb);
        ddb.Setup(d => d.GetLocalPath(It.IsAny<string>()))
            .Returns((string p) => Path.Combine(_dataset, p.Replace('/', Path.DirectorySeparatorChar)));
        ddb.Setup(d => d.GetEntry("archive.zip")).Returns(new Entry { Path = "archive.zip", Type = EntryType.Generic });
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns((string p) => _indexed.Contains(p));
        ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> paths) =>
            {
                _added.AddRange(paths);
                return [.. paths.Select(p => new Entry { Path = p, Type = EntryType.Generic })];
            });

        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.BuildQueue).Returns(Support.TestJobs.Scheduler());
        return ctx.Object;
    }

    private static IServiceScopeFactory Scope()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IBackgroundJobsProcessor>());
        services.AddSingleton(Mock.Of<IDatasetCacheInvalidator>());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // Reports inline (System.Progress<T> posts to the thread pool)
    private sealed class SyncProgress(Action<HeavyToolProgress> report) : IProgress<HeavyToolProgress>
    {
        public void Report(HeavyToolProgress value) => report(value);
    }
}

/// <summary>How the tests build the tool (the one place that knows its constructor).</summary>
internal static class Rq10ExtractionHarness
{
    public static ArchiveExtractTool CreateTool(long maxArchiveBytes, IServiceScopeFactory scopes)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings { MaxArchiveExtractSizeBytes = maxArchiveBytes }
        });
        var extractor = new SharpCompressArchiveExtractor();
        return new ArchiveExtractTool(Support.TestArchives.Extractor(extractor), scopes, Support.TestSettings.Options<ImportSettings>(settings), Support.TestSettings.Options<ProcessingPlatformSettings>(settings),
            Support.TestPaths.Policy, Support.TestHeadroom.Default, Support.TestJobs.FileIndexer());
    }
}
