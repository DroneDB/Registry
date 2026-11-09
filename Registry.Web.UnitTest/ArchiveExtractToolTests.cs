using Registry.Web.Test.Support;
using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class ArchiveExtractToolTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ddb_extracttool_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    // The tool no longer reads the storage quota itself: HeavyTaskRunner checks the plan at submit (FR-XC-26) and the worker
    // reads the context's Storage when it writes
    private static (ArchiveExtractTool tool, Mock<IStorageQuota> storage) CreateTool(
        long maxArchiveBytes = 5L * 1024 * 1024 * 1024)
    {
        var extractor = new SharpCompressArchiveExtractor();

        var storage = new Mock<IStorageQuota>();

        var settings = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings { MaxArchiveExtractSizeBytes = maxArchiveBytes }
        };

        var tool = new ArchiveExtractTool(
            TestArchives.Extractor(extractor), TestSettings.Options<ImportSettings>(Microsoft.Extensions.Options.Options.Create(settings)), TestSettings.Options<ProcessingPlatformSettings>(Microsoft.Extensions.Options.Options.Create(settings)),
            TestPaths.Policy,
            TestHeadroom.Default,
            TestJobs.FileIndexer());

        return (tool, storage);
    }

    private static IHeavyToolValidationContext Ctx(IDDB ddb, bool withCaller = true)
    {
        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb);
        // At submit time the caller is the authenticated principal; on the Hangfire
        // worker it is null. The quota guard only runs when the caller is present.
        ctx.SetupGet(c => c.Caller)
            .Returns(withCaller ? new ClaimsPrincipal(new ClaimsIdentity()) : null);
        return ctx.Object;
    }

    private static HeavyToolRequest Request(object prms)
    {
        var element = JsonSerializer.SerializeToElement(prms);
        return new HeavyToolRequest("archive-extract", "1", "org", "ds", null, element);
    }

    private Mock<IDDB> MockDdb(string archiveRelPath, string localArchivePath, EntryType type = EntryType.Generic, long size = 0)
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dir);
        ddb.Setup(d => d.GetEntry(archiveRelPath)).Returns(new Entry { Path = archiveRelPath, Type = type, Size = size });
        ddb.Setup(d => d.GetLocalPath(archiveRelPath)).Returns(localArchivePath);
        return ddb;
    }

    [Test]
    public async Task ValidateAsync_ValidZip_Passes_WithoutReadingTheQuota()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, storage) = CreateTool();

        await tool.ValidateAsync(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object), CancellationToken.None);

        // The storage quota is the gate of HeavyTaskRunner.CheckSubmitAsync, not of the tool (FR-XC-26)
        storage.VerifyNoOtherCalls();
    }

    [Test]
    public void ValidateAsync_MissingSource_Throws()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dir);
        ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns((Entry)null!);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "missing.zip" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_UnsupportedFormat_Throws()
    {
        var local = Path.Combine(_dir, "photo.jpg");
        File.WriteAllText(local, "not an archive");
        var ddb = MockDdb("photo.jpg", local);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "photo.jpg" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_ReservedDestPath_Throws()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = ".ddb/evil" }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    // The reserved rule is libddb's, a ".ddb" segment at any depth (libddb would skip it).
    [TestCase("sub/.ddb")]
    [TestCase(@"sub\.ddb\evil")]
    public void ValidateAsync_NestedReservedDestPath_Throws(string destPath)
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    // ".ddbx" only starts like the index folder; it is an ordinary folder.
    [Test]
    public async Task ValidateAsync_DdbLookalikeDestPath_Passes()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        await tool.ValidateAsync(
            Request(new { sourcePath = "flight.zip", destPath = ".ddbx" }),
            Ctx(ddb.Object), CancellationToken.None);
    }

    [Test]
    public void ValidateAsync_DestPathTraversal_Throws()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<InvalidDatasetPathException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = "../escape" }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ValidateAsync_DestPathIsFile_Is409WithItsPath_WhateverOverwriteSays(bool overwrite)
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        // The destination resolves to an existing non-folder entry.
        ddb.Setup(d => d.EntryExists("existing.txt")).Returns(true);
        ddb.Setup(d => d.GetEntry("existing.txt"))
            .Returns(new Entry { Path = "existing.txt", Type = EntryType.Generic });

        var (tool, _) = CreateTool();

        var ex = Should.Throw<NameClashException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = "existing.txt", overwrite }),
                Ctx(ddb.Object), CancellationToken.None));
        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldBe("A file or folder named 'existing.txt' already exists in this folder");
        ex.ConflictPath.ShouldBe("existing.txt");
    }

    [Test]
    public void ValidateAsync_DestPathIsAFileInAnotherLetterCase_Is409WithTheExistingPath()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q =>
            string.IsNullOrEmpty(q.Path) ? [new EntrySummary { Path = "Existing.TXT", Type = EntryType.Generic }] : []);

        var (tool, _) = CreateTool();

        Should.Throw<NameClashException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = "existing.txt" }),
                Ctx(ddb.Object), CancellationToken.None)).ConflictPath.ShouldBe("Existing.TXT");
    }

    [Test]
    public void Label_NamesTheArchiveAndTheFolder_AndNoOutputIsDeclared()
    {
        var (tool, _) = CreateTool();

        tool.Label(Request(new { sourcePath = "drops/flight.zip", destPath = "photos" }))
            .ShouldBe("drops/flight.zip \u2192 photos/");
        tool.Label(Request(new { sourcePath = "flight.zip" })).ShouldBe("flight.zip \u2192 /");
        tool.OutputPaths(Request(new { sourcePath = "flight.zip", destPath = "photos" })).ShouldBeEmpty();
    }

    [Test]
    public void ValidateAsync_ArchiveTooLarge_Throws()
    {
        var local = Path.Combine(_dir, "big.zip");
        CreateZip(local, "a.txt", "hello");
        // Report a huge archive size via the index entry.
        var ddb = MockDdb("big.zip", local, size: 10L * 1024 * 1024 * 1024);

        var (tool, _) = CreateTool(maxArchiveBytes: 5L * 1024 * 1024 * 1024);

        var ex = Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "big.zip" }), Ctx(ddb.Object), CancellationToken.None));
        ex.Message.ShouldContain("too large");
    }

    [Test]
    public void Plan_ReturnsUncompressedEstimate()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        var plan = tool.Plan(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object));

        plan.QuotaKey.ShouldBe("archive-extract");
        plan.EstimatedOutputBytes.ShouldBe(5); // "hello"
    }

    // FR-PRC-16: an entry outside the dataset stops the extraction; the task error names the entry and says that
    // nothing was extracted (JobLogText.UserMessage, as HeavyTaskJobWrapper records it)
    [Test]
    public async Task ExecuteAsync_AnEntryOutsideTheDataset_FailsWithAnErrorThatNamesIt()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "../escape.txt", "evil");
        var ddb = MockDdb("flight.zip", local);
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.Storage).Returns(Registry.Web.Services.Ports.UnlimitedStorageQuota.Instance);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("org", Guid.NewGuid()));
        ctx.SetupGet(c => c.WriteRecord).Returns(new FakeTaskWriteRecorder());
        var (tool, _) = CreateTool();

        var ex = await Should.ThrowAsync<InvalidDatasetPathException>(() => tool.ExecuteAsync(
            Request(new { sourcePath = "flight.zip" }), ctx.Object, new Progress<HeavyToolProgress>(),
            CancellationToken.None));

        Registry.Web.Services.Jobs.JobLogText.UserMessage(ex, _dir)
            .ShouldBe("The archive contains an unsafe path: ../escape.txt. Nothing was extracted.");
        File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escape.txt")).ShouldBeFalse();
    }

    // The archive could not be deleted after the extraction: the log line says why, without server paths
    [Test]
    public async Task ExecuteAsync_AnArchiveThatCannotBeDeleted_IsLoggedWithoutServerPaths()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        ddb.Setup(d => d.Remove("flight.zip")).Throws(new IOException(
            $"Cannot remove {local}: locked by /var/lib/registry/locks/flight.lock"));
        // Nothing indexed: no build to queue (the indexer is not what this test is about)
        ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>())).Returns([]);
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.Storage).Returns(Registry.Web.Services.Ports.UnlimitedStorageQuota.Instance);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("org", Guid.NewGuid()));
        ctx.SetupGet(c => c.WriteRecord).Returns(new FakeTaskWriteRecorder());
        var lines = new System.Collections.Generic.List<string>();
        var progress = new SyncLog(lines);
        var (tool, _) = CreateTool();

        await tool.ExecuteAsync(Request(new { sourcePath = "flight.zip", deleteArchive = true }), ctx.Object,
            progress, CancellationToken.None);

        var line = lines.Single(l => l.StartsWith("Could not delete source archive", StringComparison.Ordinal));
        line.ShouldBe("Could not delete source archive 'flight.zip': " +
                      $"Cannot remove {Registry.Web.Services.Jobs.JobLogText.DatasetPlaceholder}" +
                      $"{Path.DirectorySeparatorChar}flight.zip: locked by [server path]");
        line.ShouldNotContain(_dir);
    }

    private static Mock<IHeavyToolExecutionContext> ExecutionContext(Mock<IDDB> ddb, IStorageQuota? storage = null)
    {
        ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>())).Returns([]);
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb.Object);
        ctx.SetupGet(c => c.Storage).Returns(storage ?? UnlimitedStorageQuota.Instance);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("org", Guid.NewGuid()));
        ctx.SetupGet(c => c.WriteRecord).Returns(new FakeTaskWriteRecorder());
        return ctx;
    }

    private static Mock<IStorageQuota> OwnerWithLeft(long remaining)
    {
        var storage = new Mock<IStorageQuota>();
        storage.Setup(q => q.OwnerIdOfAsync("org", It.IsAny<CancellationToken>())).ReturnsAsync("owner-1");
        storage.Setup(q => q.RemainingBytesAsync("owner-1", It.IsAny<CancellationToken>())).ReturnsAsync(remaining);
        return storage;
    }

    // FR-XC-26, FR-PRC-08: the worker reads the remaining bytes of the owner of the organization when it writes; an archive
    // that does not fit stops, the task fails with the 507 text, and no file of the extraction stays
    [Test]
    public async Task ExecuteAsync_AnArchiveThatDoesNotFitTheQuotaOfTheOwner_Fails507_AndLeavesNoFile()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello world");
        var ddb = MockDdb("flight.zip", local);
        var (tool, _) = CreateTool();

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => tool.ExecuteAsync(
            Request(new { sourcePath = "flight.zip" }), ExecutionContext(ddb, OwnerWithLeft(4).Object).Object,
            new Progress<HeavyToolProgress>(), CancellationToken.None));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 4 B left.");
        Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName)
            .ShouldBe(["flight.zip"], "only the archive: no file of the extraction stays");
    }

    [Test]
    public async Task ExecuteAsync_AnOwnerWithNothingLeft_FailsBeforeWriting()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        var (tool, _) = CreateTool();

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => tool.ExecuteAsync(
            Request(new { sourcePath = "flight.zip" }), ExecutionContext(ddb, OwnerWithLeft(0).Object).Object,
            new Progress<HeavyToolProgress>(), CancellationToken.None));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 0 B left.");
    }

    [Test]
    public async Task ExecuteAsync_AnArchiveThatFits_IsExtracted()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);
        var (tool, _) = CreateTool();

        await tool.ExecuteAsync(Request(new { sourcePath = "flight.zip" }), ExecutionContext(ddb, OwnerWithLeft(5).Object).Object,
            new Progress<HeavyToolProgress>(), CancellationToken.None);

        File.ReadAllText(Path.Combine(_dir, "a.txt")).ShouldBe("hello");
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    // FR-XC-26: with overwrite, the files the extraction replaces are counted in the owner's use already (their index
    // entries): the plan carries their size, so the submit checks only the growth; without overwrite nothing is replaced
    [Test]
    public void Plan_WithOverwrite_CarriesTheIndexedSizeOfTheFilesItReplaces()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, ("a.txt", "hello"), ("b.txt", "world!!"), ("tool.exe", "x"));
        var ddb = MockDdb("flight.zip", local);
        ddb.Setup(d => d.GetEntry("a.txt")).Returns(new Entry { Path = "a.txt", Type = EntryType.Generic, Size = 3 });
        // Refused by the import policy: not written, so not replaced
        ddb.Setup(d => d.GetEntry("tool.exe")).Returns(new Entry { Path = "tool.exe", Type = EntryType.Generic, Size = 100 });
        var (tool, _) = CreateTool();

        var replace = tool.Plan(Request(new { sourcePath = "flight.zip", overwrite = true }), Ctx(ddb.Object));
        var keep = tool.Plan(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object));

        replace.EstimatedOutputBytes.ShouldBe(13);
        replace.ReplacedBytes.ShouldBe(3);
        replace.StorageGrowthBytes.ShouldBe(10);
        keep.ReplacedBytes.ShouldBe(0);
    }

    // FR-XC-26: the worker cap counts only the growth of a replace: the owner has 8 bytes left, the archive writes 11 over a
    // file the owner's use counts as 3
    [Test]
    public async Task ExecuteAsync_WithOverwrite_FitsWhenTheGrowthFits()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello world");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "old");
        var ddb = MockDdb("flight.zip", local);
        ddb.Setup(d => d.GetEntry("a.txt")).Returns(new Entry { Path = "a.txt", Type = EntryType.Generic, Size = 3 });
        ddb.Setup(d => d.EntryExists("a.txt")).Returns(true);
        var (tool, _) = CreateTool();

        await tool.ExecuteAsync(Request(new { sourcePath = "flight.zip", overwrite = true }),
            ExecutionContext(ddb, OwnerWithLeft(8).Object).Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

        File.ReadAllText(Path.Combine(_dir, "a.txt")).ShouldBe("hello world");
    }

    [Test]
    public async Task ExecuteAsync_WithOverwrite_WhenTheGrowthDoesNotFit_Fails507_AndKeepsTheFileItWouldReplace()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello world");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "old");
        var ddb = MockDdb("flight.zip", local);
        ddb.Setup(d => d.GetEntry("a.txt")).Returns(new Entry { Path = "a.txt", Type = EntryType.Generic, Size = 3 });
        ddb.Setup(d => d.EntryExists("a.txt")).Returns(true);
        var (tool, _) = CreateTool();

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => tool.ExecuteAsync(
            Request(new { sourcePath = "flight.zip", overwrite = true }), ExecutionContext(ddb, OwnerWithLeft(7).Object).Object,
            new Progress<HeavyToolProgress>(), CancellationToken.None));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 7 B left.");
        File.ReadAllText(Path.Combine(_dir, "a.txt")).ShouldBe("old");
    }

    // FR-PRC-30: the log names the entries that were skipped with the reason, apart from the extracted ones
    [Test]
    public async Task ExecuteAsync_LogsTheSkippedEntriesWithTheirReason()
    {
        var local = Path.Combine(_dir, "good.zip");
        using (var fs = File.Create(local))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in new[] { ("a.txt", "a"), ("vectors/tool.exe", "x") })
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        var ddb = MockDdb("good.zip", local);
        var lines = new System.Collections.Generic.List<string>();
        var (tool, _) = CreateTool();

        await tool.ExecuteAsync(Request(new { sourcePath = "good.zip" }), ExecutionContext(ddb).Object,
            new SyncLog(lines), CancellationToken.None);

        var log = string.Join('\n', lines).Split('\n');
        log.ShouldContain("Skipped: vectors/tool.exe (file type not allowed)");
        log.ShouldContain("Extraction complete: 1 extracted, 1 skipped");
        File.Exists(Path.Combine(_dir, "vectors", "tool.exe")).ShouldBeFalse();
    }

    private sealed class SyncLog(System.Collections.Generic.List<string> lines) : IProgress<HeavyToolProgress>
    {
        public void Report(HeavyToolProgress value)
        {
            if (value.LogChunk != null) lines.Add(value.LogChunk);
        }
    }

    private static void CreateZip(string path, string entryName, string content)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
