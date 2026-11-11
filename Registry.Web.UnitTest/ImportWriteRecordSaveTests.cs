using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The save point of an import (§8.1) goes through <see cref="IImportWriteRecord.SaveAsync"/>, not through the type of
/// the write record: a writer records a batch, saves, and only then creates the files, with whatever write record it
/// is given, and the failure of a save stops the writer before it creates anything.
/// </summary>
[TestFixture]
public class ImportWriteRecordSaveTests
{
    private string _root = null!;
    private string _dataset = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "import-write-save-" + Guid.NewGuid().ToString("N"));
        _dataset = Path.Combine(_root, "dataset");
        Directory.CreateDirectory(_dataset);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private static readonly string[] AllThree = ["a.txt", "b.txt", "c.txt"];

    private string DatasetFile(string path) => Path.Combine(_dataset, path);

    private static RemoteRegistryClient Client(int batchSize)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new DataHandler(), false));
        return new RemoteRegistryClient(factory.Object,
            new ImportSettings { RegistryDownloadConcurrency = 1, RegistryDownloadMaxRetries = 1 },
            TestPaths.Policy, TestScratch.Provider, NullLogger<RemoteRegistryClient>.Instance)
        {
            PlaceBatchSize = batchSize
        };
    }

    private static EntryDto[] Entries(params string[] paths) =>
        [.. paths.Select(p => new EntryDto { Path = p, Type = EntryType.Image, Size = 4 })];

    private static DatasetArchiveExtractor Extractor(int batchSize) =>
        new(new Registry.Adapters.Archives.SharpCompressArchiveExtractor(), TestPaths.Policy, TestScratch.Provider,
            new Registry.Adapters.FileSystem(), TestHeadroom.Default, NullLogger<DatasetArchiveExtractor>.Instance)
        {
            PlaceBatchSize = batchSize
        };

    private string Archive(params string[] names)
    {
        var archive = Path.Combine(_root, "in.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write("x"u8);
        }

        return archive;
    }

    [Test]
    public async Task TheRegistryImport_SavesEachBatchThroughTheInterface_BeforeItCreatesItsFiles()
    {
        var writes = new SavingWrites(_dataset);
        var paths = new[] { "a.jpg", "b.jpg", "sub/c.jpg" };

        await Client(batchSize: 2).DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset,
            Entries(paths), writes, null, CancellationToken.None);

        // Two batches (2 + 1 files): each one is recorded and saved, and only then is it on disk
        writes.Saves.Count.ShouldBe(2);
        writes.Saves[0].Recorded.Order(StringComparer.Ordinal).ShouldBe(["a.jpg", "b.jpg"]);
        writes.Saves[0].OnDisk.ShouldBeEmpty("nothing of the batch exists before the record is saved");
        writes.Saves[1].Recorded.ShouldBe(["sub/c.jpg"]);
        writes.Saves[1].OnDisk.Order(StringComparer.Ordinal).ShouldBe(["a.jpg", "b.jpg"]);
        writes.Saves[1].Folders.ShouldBe(["sub"]);
        paths.ShouldAllBe(p => File.Exists(DatasetFile(p)));
    }

    [Test]
    public async Task TheRegistryImport_DoesNotSave_WhenEveryFileIsKept()
    {
        File.WriteAllText(DatasetFile("a.jpg"), "mine");
        var writes = new SavingWrites(_dataset, mayWrite: false);

        await Client(batchSize: 2).DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset,
            Entries("a.jpg"), writes, null, CancellationToken.None);

        writes.Saves.ShouldBeEmpty("nothing is to be created, so nothing is saved");
        File.ReadAllText(DatasetFile("a.jpg")).ShouldBe("mine");
    }

    [Test]
    public async Task TheRegistryImport_CreatesNothing_WhenTheSaveFails()
    {
        var writes = new SavingWrites(_dataset) { SaveFailure = new InvalidOperationException("record closed") };

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Client(batchSize: 2)
            .DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset, Entries("a.jpg", "sub/b.jpg"),
                writes, null, CancellationToken.None));

        failure.Message.ShouldBe("record closed");
        writes.Saves.Count.ShouldBe(1);
        File.Exists(DatasetFile("a.jpg")).ShouldBeFalse();
        Directory.Exists(DatasetFile("sub")).ShouldBeFalse();
    }

    [Test]
    public async Task TheArchiveExtraction_SavesEachBatchThroughTheInterface_BeforeItPlacesTheFiles()
    {
        var writes = new SavingWrites(_dataset);
        var archive = Archive("a.txt", "b.txt", "c.txt");

        await Extractor(batchSize: 2).ExtractAsync(
            new ArchiveExtractionRequest(archive, _dataset, string.Empty, _ => true, _ => false, 0, 0, writes),
            null, CancellationToken.None);

        writes.Saves.Count.ShouldBe(2);
        writes.Saves[0].Recorded.Order(StringComparer.Ordinal).ShouldBe(["a.txt", "b.txt"]);
        writes.Saves[0].OnDisk.ShouldBeEmpty("nothing of the batch is placed before the record is saved");
        writes.Saves[1].Recorded.ShouldBe(["c.txt"]);
        writes.Saves[1].OnDisk.Order(StringComparer.Ordinal).ShouldBe(["a.txt", "b.txt"]);
        AllThree.ShouldAllBe(p => File.Exists(DatasetFile(p)));
    }

    [Test]
    public async Task TheArchiveExtraction_PlacesNothing_WhenTheSaveFails()
    {
        var writes = new SavingWrites(_dataset) { SaveFailure = new InvalidOperationException("record closed") };
        var archive = Archive("a.txt", "deep/b.txt");

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Extractor(batchSize: 10).ExtractAsync(
            new ArchiveExtractionRequest(archive, _dataset, string.Empty, _ => true, _ => false, 0, 0, writes),
            null, CancellationToken.None));

        failure.Message.ShouldBe("record closed");
        writes.Saves.Count.ShouldBe(1);
        File.Exists(DatasetFile("a.txt")).ShouldBeFalse();
        File.Exists(DatasetFile("deep/b.txt")).ShouldBeFalse();
    }

    [Test]
    public async Task TheTaskWriteRecord_SavesItsRecorderThroughTheInterface()
    {
        var recorder = new FakeTaskWriteRecorder();
        var own = await TaskImportWrites.StartAsync(recorder,
            new DatasetKey("org", Guid.NewGuid()), _dataset, CancellationToken.None);

        await RecordAndSaveAsync(own, "a.jpg", () => recorder.SavedPaths.ShouldBeEmpty("recorded, not saved yet"));

        recorder.SavedPaths.ShouldBe(["a.jpg"]);
        recorder.Flushes.ShouldBe(1);
    }

    // Only through the interface, as a source sees the write record
#pragma warning disable CA1859 // the interface type is the point of the test
    private static async Task RecordAndSaveAsync(IImportWriteRecord writes, string path, Action beforeSave)
    {
        writes.Record(path);
        beforeSave();
        await writes.SaveAsync(CancellationToken.None);
    }
#pragma warning restore CA1859

    private sealed class DataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent("data"u8.ToArray()) });
    }

    /// <summary>A write record that is not the task's: it only records what is called, and when, in the dataset.</summary>
    private sealed class SavingWrites(string root, bool mayWrite = true) : IImportWriteRecord
    {
        private readonly List<string> _pending = [];
        private readonly List<string> _pendingFolders = [];

        public sealed record SavePoint(string[] Recorded, string[] Folders, string[] OnDisk);

        public List<SavePoint> Saves { get; } = [];
        public Exception? SaveFailure { get; init; }

        public bool MayWrite(string path) => mayWrite;
        public void Record(string path) => _pending.Add(path);
        public void RecordFolder(string path) => _pendingFolders.Add(path);
        public void Written(string path) { }
        public void Written(string path, string localFile) { }
        public Task ForgetAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public ValueTask FlushIfDueAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public Task SaveAsync(CancellationToken ct)
        {
            Saves.Add(new SavePoint([.. _pending], [.. _pendingFolders], OnDisk()));
            _pending.Clear();
            _pendingFolders.Clear();
            return SaveFailure is null ? Task.CompletedTask : Task.FromException(SaveFailure);
        }

        // The files of the dataset (outside the uploads folder of the temporary files)
        private string[] OnDisk() =>
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith(Registry.Ports.DroneDB.IDDB.UploadsFolderName + "/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
    }
}
