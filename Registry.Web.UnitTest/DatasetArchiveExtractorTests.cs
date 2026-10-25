#nullable enable
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
        result.Skipped.ShouldBe(1);
        Directory.Exists(Path.Combine(_dataset, IDDB.UploadsFolderName)).ShouldBeFalse();
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

    private Task<ArchiveExtractionResult> ExtractAsync(VolumeHeadroom? headroom = null, long safetyMargin = 0,
        IProgress<ArchiveExtractionProgress>? progress = null) =>
        TestArchives.Extractor(new SharpCompressArchiveExtractor(), headroom).ExtractAsync(
            new ArchiveExtractionRequest(_archive, _dataset, string.Empty, _ => true, _ => false, 0, safetyMargin),
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
