using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// An extraction places each name under the name rule (letter case ignored), which looks each name up in the dataset.
/// The cost of that lookup must follow the number of folders, not the number of entries: the names of a folder are
/// listed once, and the names the extraction claims are added to that view, so thousands of entries in one folder
/// do not read the folder again for each of them.
/// </summary>
[TestFixture]
public class ArchiveExtractionLargeFolderTests
{
    private string _dataset = null!;
    private string _archive = null!;
    private int _queries;
    private Mock<IDDB> _ddb = null!;

    [SetUp]
    public void SetUp()
    {
        _dataset = Path.Combine(Path.GetTempPath(), "bigfolder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataset);
        _archive = Path.Combine(Path.GetTempPath(), "bigfolder-" + Guid.NewGuid().ToString("N") + ".zip");

        // An index that lists nothing, over the folder of the dataset: every name is told by the disk, as for files that an
        // interrupted write left or that were copied in by hand
        _queries = 0;
        _ddb = new Mock<IDDB>();
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns((string p) => Path.Combine(_dataset, p));
        _ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns(() =>
        {
            Interlocked.Increment(ref _queries);
            return [];
        });
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataset)) Directory.Delete(_dataset, true);
        if (File.Exists(_archive)) File.Delete(_archive);
    }

    private void Zip(params string[] names)
    {
        using var fs = File.Create(_archive);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            entry.WriteByte(1);
        }
    }

    private async Task<ArchiveExtractionResult> ExtractAsync(bool overwrite)
    {
        var sw = Stopwatch.StartNew();
        var result = await TestArchives.Extractor(new SharpCompressArchiveExtractor()).ExtractAsync(
            new ArchiveExtractionRequest(_archive, _dataset, string.Empty, _ => true,
                location => location.Existing is { } existing && (!overwrite || existing.IsFolder), 0, 0,
                Dataset: _ddb.Object), null, CancellationToken.None);
        TestContext.Out.WriteLine($"{result.Extracted.Count} extracted in {sw.ElapsedMilliseconds} ms, {_queries} index queries");
        return result;
    }

    [Test]
    public async Task ManyNewEntriesInOneFolder_AskTheIndexOncePerFolder_NotOncePerEntry()
    {
        const int count = 3000;
        Zip([.. Enumerable.Range(0, count).Select(i => $"photos/IMG_{i:00000}.jpg")]);

        var result = await ExtractAsync(overwrite: false);

        result.Extracted.Count.ShouldBe(count);
        Directory.GetFiles(Path.Combine(_dataset, "photos")).Length.ShouldBe(count);
        // The root and the folder: nothing more, whatever the number of entries
        _queries.ShouldBeLessThanOrEqualTo(2);
    }

    // The folder holds files already (not indexed): an entry that differs from one only in letter case is that file, so an
    // overwrite replaces it in place and no second file appears; the other entries are new
    [Test]
    public async Task EntriesIntoAFolderThatHoldsManyFiles_ReplaceThoseThatDifferOnlyInLetterCase_InPlace()
    {
        const int existing = 1500;
        Directory.CreateDirectory(Path.Combine(_dataset, "Photos"));
        for (var i = 0; i < existing; i++)
            File.WriteAllText(Path.Combine(_dataset, "Photos", $"IMG_{i:00000}.JPG"), "old");
        Zip([.. Enumerable.Range(0, 2 * existing).Select(i => $"photos/img_{i:00000}.jpg")]);

        var result = await ExtractAsync(overwrite: true);

        result.Extracted.Count.ShouldBe(2 * existing);
        result.Extracted.Take(existing).ShouldAllBe(p => p.StartsWith("Photos/IMG_", StringComparison.Ordinal));
        Directory.GetDirectories(_dataset).Select(Path.GetFileName).Where(n => n != IDDB.UploadsFolderName)
            .ShouldBe(["Photos"]);
        Directory.GetFiles(Path.Combine(_dataset, "Photos")).Length.ShouldBe(2 * existing);
        File.ReadAllBytes(Path.Combine(_dataset, "Photos", "IMG_00000.JPG")).ShouldBe([1]);
        _queries.ShouldBeLessThanOrEqualTo(2);
    }
}
