using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="DatasetArchiveExtractor"/>, the one extraction loop: the URL import's use
/// (into the dataset root, always overwriting), duplicate names, the disk head-room and the rollback
/// of a replaced file that was not indexed. The tool-level behaviour is in
/// <c>ArchiveExtractionTests</c>.
/// </summary>
[TestFixture]
public class DatasetArchiveExtractorTests
{
    private const long MiB = 1024L * 1024;
    private string _dataset = null!;
    private string _archive = null!;

    [SetUp]
    public void SetUp()
    {
        _dataset = Path.Combine(Path.GetTempPath(), "extractor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataset);
        _archive = Path.Combine(Path.GetTempPath(), "extractor-" + Guid.NewGuid().ToString("N") + ".zip");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataset)) Directory.Delete(_dataset, true);
        if (File.Exists(_archive)) File.Delete(_archive);
    }

    [Test]
    public async Task IntoTheRoot_NeverWritesIntoTheUploadsFolder()
    {
        Zip(("a.txt", "a"), (".uploads/x.tmp", "x"), ("photos/.uploads/y.jpg", "y"));

        var result = await ExtractAsync();

        result.Extracted.ShouldBe(["a.txt", "photos/.uploads/y.jpg"]);
        result.Skipped.ShouldBe([new ArchiveSkippedEntry(".uploads/x.tmp", ArchiveSkipReason.ReservedPath)]);
        // The uploads folder holds only the extraction's own temporary files while it runs: none is left, and no entry
        var uploads = Path.Combine(_dataset, IDDB.UploadsFolderName);
        (Directory.Exists(uploads) ? Directory.GetFileSystemEntries(uploads) : []).ShouldBeEmpty();
    }

    [Test]
    public async Task DuplicateNames_TheLastWins_AndIsListedOnce()
    {
        Zip(("a/b.txt", "first"), (@"a\b.txt", "second"));

        var result = await ExtractAsync();

        result.Extracted.ShouldBe(["a/b.txt"]);
        File.ReadAllText(Path.Combine(_dataset, "a", "b.txt")).ShouldBe("second");
    }

    [Test]
    public async Task InBatchesOfOne_EveryEntryIsPlaced_AndADuplicateInALaterBatchWins()
    {
        Zip(("a/b.txt", "first"), ("c.txt", "c"), (@"a\b.txt", "second"));
        var extractor = new DatasetArchiveExtractor(new SharpCompressArchiveExtractor(), TestPaths.Policy,
            TestScratch.Provider, new Registry.Adapters.FileSystem(), TestHeadroom.Default,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatasetArchiveExtractor>.Instance)
        {
            PlaceBatchSize = 1
        };

        var result = await extractor.ExtractAsync(
            new ArchiveExtractionRequest(_archive, _dataset, string.Empty, _ => true, _ => false, 0, 0), null,
            CancellationToken.None);

        result.Extracted.ShouldBe(["a/b.txt", "c.txt"]);
        File.ReadAllText(Path.Combine(_dataset, "a", "b.txt")).ShouldBe("second");
        var uploads = Path.Combine(_dataset, IDDB.UploadsFolderName);
        (Directory.Exists(uploads) ? Directory.GetFileSystemEntries(uploads) : []).ShouldBeEmpty();
    }

    [Test]
    public async Task Failure_PutsBackAFileThatWasNotIndexed()
    {
        File.WriteAllText(Path.Combine(_dataset, "a.txt"), "stray");
        Zip(("a.txt", "new"), ("../x.txt", "evil"));

        await Should.ThrowAsync<InvalidDatasetPathException>(() => ExtractAsync());

        File.ReadAllText(Path.Combine(_dataset, "a.txt")).ShouldBe("stray");
        Directory.GetFiles(_dataset, "*", SearchOption.AllDirectories).Length.ShouldBe(1, "no backup is left behind");
    }

    [Test]
    public async Task DiskHeadroomReached_RollsBack()
    {
        Zip(("a.txt", "a"), ("b.bin", new string('b', 64 * 1024)));
        // 300 MiB free, 256 MiB margin + 64 MiB floor: no room for anything
        var headroom = TestHeadroom.WithFree(300 * MiB, floorMb: 64);

        await Should.ThrowAsync<QuotaExceededException>(() => ExtractAsync(headroom, safetyMargin: 256 * MiB));

        Directory.GetFiles(_dataset, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Test]
    public async Task Success_ReportsEveryEntry()
    {
        Zip(("a.txt", "a"), (".ddb/x", "x"), ("b.txt", "b"));
        var reports = new System.Collections.Generic.List<ArchiveExtractionProgress>();

        await ExtractAsync(progress: new SyncProgress(reports.Add));

        reports.Select(r => r.Done).ShouldBe([0, 1, 2, 3]);
        reports.Select(r => r.Total).Distinct().ShouldBe([3]);
        reports[^1].BytesWritten.ShouldBe(2);
    }

    // FR-PRC-30: every entry that is not extracted is listed with its dataset path and its reason; the log names those
    // entries as skipped, never as extracted
    [Test]
    public async Task Skipped_NamesEveryEntryThatIsNotExtracted_WithItsReason()
    {
        File.WriteAllText(Path.Combine(_dataset, "keep.txt"), "mine");
        Zip(("good.txt", "g"), ("vectors/tool.exe", "x"), ("keep.txt", "new"), (".ddb/x", "x"));

        var result = await ExtractAsync(isExtensionAllowed: path => !path.EndsWith(".exe", StringComparison.Ordinal),
            keepExisting: path => path == "keep.txt");

        result.Extracted.ShouldBe(["good.txt"]);
        result.Skipped.ShouldBe(
        [
            new ArchiveSkippedEntry("vectors/tool.exe", ArchiveSkipReason.FileTypeNotAllowed),
            new ArchiveSkippedEntry("keep.txt", ArchiveSkipReason.KeptExisting),
            new ArchiveSkippedEntry(".ddb/x", ArchiveSkipReason.ReservedPath)
        ]);
        File.ReadAllText(Path.Combine(_dataset, "keep.txt")).ShouldBe("mine");
        result.LogLines().ShouldBe(
        [
            "Skipped: vectors/tool.exe (file type not allowed)",
            "Skipped: keep.txt (file already exists)",
            "Skipped: .ddb/x (reserved path)",
            "Extraction complete: 1 extracted, 3 skipped"
        ]);
    }

    // FR-PRC-30: the progress message names only a file that is written
    [Test]
    public async Task Progress_NamesOnlyTheEntriesThatAreWritten()
    {
        Zip(("a.txt", "a"), ("tool.exe", "x"));
        var reports = new System.Collections.Generic.List<ArchiveExtractionProgress>();

        await ExtractAsync(progress: new SyncProgress(reports.Add),
            isExtensionAllowed: path => !path.EndsWith(".exe", StringComparison.Ordinal));

        reports.Select(r => r.Key).ShouldBe(["", "a.txt", ""]);
    }

    // FR-PRC-41: the Exclude predicate is apart from the extension policy and has its own reason; the entries are
    // counted in the log, not named one by one
    [Test]
    public async Task Exclude_SkipsTheEptChunks_WithTheReasonExcluded()
    {
        Zip(("odm_orthophoto/odm_orthophoto.tif", "o"), ("entwine_pointcloud/ept-data/0-0-0-0.laz", "l"),
            ("entwine_pointcloud/ept.json", "j"), ("opensfm/reconstruction.json", "r"));

        var result = await ExtractAsync(exclude: path =>
            path.StartsWith("entwine_pointcloud/", StringComparison.Ordinal) ||
            path.StartsWith("opensfm/", StringComparison.Ordinal));

        result.Extracted.ShouldBe(["odm_orthophoto/odm_orthophoto.tif"]);
        result.Skipped.ShouldBe(
        [
            new ArchiveSkippedEntry("entwine_pointcloud/ept-data/0-0-0-0.laz", ArchiveSkipReason.Excluded),
            new ArchiveSkippedEntry("entwine_pointcloud/ept.json", ArchiveSkipReason.Excluded),
            new ArchiveSkippedEntry("opensfm/reconstruction.json", ArchiveSkipReason.Excluded)
        ]);
        Directory.Exists(Path.Combine(_dataset, "entwine_pointcloud")).ShouldBeFalse();
        result.LogLines().ShouldBe(
        [
            "Excluded 3 internal file(s) of the archive",
            "Extraction complete: 1 extracted, 3 skipped"
        ]);
    }

    // FR-XC-26: MaxBytes counts only the entries that the extraction writes; the failure says what the caller said
    [Test]
    public async Task MaxBytes_CountsOnlyWrittenEntries()
    {
        // A large entry that is excluded does not count against the cap
        Zip(("a.txt", "12345"), ("entwine_pointcloud/big.laz", new string('x', 4096)));

        var result = await ExtractAsync(maxBytes: 5, exclude: path => path.StartsWith("entwine_pointcloud/", StringComparison.Ordinal));

        result.Extracted.ShouldBe(["a.txt"]);
        result.BytesWritten.ShouldBe(5);
    }

    [Test]
    public async Task MaxBytesReached_FailsWithTheCallersText_AndNothingStays()
    {
        Zip(("a.txt", "12345"), ("b.txt", "67890"), ("c.txt", "abcde"));
        var text = QuotaExceededException.OwnerOverQuotaMessage(8);

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => ExtractAsync(maxBytes: 8, maxBytesMessage: text));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 8 B left.");
        Directory.GetFiles(_dataset, "*", SearchOption.AllDirectories).ShouldBeEmpty("no file of the extraction stays");
    }

    private Task<ArchiveExtractionResult> ExtractAsync(VolumeHeadroom? headroom = null, long safetyMargin = 0,
        IProgress<ArchiveExtractionProgress>? progress = null, Func<string, bool>? isExtensionAllowed = null,
        Func<string, bool>? keepExisting = null, Func<string, bool>? exclude = null, long maxBytes = 0,
        string? maxBytesMessage = null) =>
        TestArchives.Extractor(new SharpCompressArchiveExtractor(), headroom).ExtractAsync(
            new ArchiveExtractionRequest(_archive, _dataset, string.Empty, isExtensionAllowed ?? (_ => true),
                keepExisting ?? (_ => false), maxBytes, safetyMargin, Exclude: exclude, MaxBytesMessage: maxBytesMessage),
            progress, CancellationToken.None);

    private void Zip(params (string Name, string Content)[] entries)
    {
        using var file = File.Create(_archive);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    private sealed class SyncProgress(Action<ArchiveExtractionProgress> report) : IProgress<ArchiveExtractionProgress>
    {
        public void Report(ArchiveExtractionProgress value) => report(value);
    }
}
