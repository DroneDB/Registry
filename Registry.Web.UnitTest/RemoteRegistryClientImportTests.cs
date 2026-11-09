using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
/// The import of a remote registry through the write record of its task (FR-XC-27, D-045): every file goes to a temporary
/// file of the dataset's uploads folder, is recorded and stamped, then moved into place without replacing anything; a
/// cancellation is a cancellation (the quota stop of D-039, the user's cancel), and what it leaves in the dataset is
/// all in the record.
/// </summary>
[TestFixture]
public class RemoteRegistryClientImportTests
{
    private static readonly DatasetKey Dataset = new("org", Guid.NewGuid());

    private string _root = null!;
    private string _dataset = null!;
    private FakeTaskWriteRecorder _recorder = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "remote-client-import-" + Guid.NewGuid().ToString("N"));
        _dataset = Path.Combine(_root, "dataset");
        Directory.CreateDirectory(_dataset);
        _recorder = new FakeTaskWriteRecorder();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private static EntryDto[] Entries(params string[] paths) =>
        [.. paths.Select(p => new EntryDto { Path = p, Type = EntryType.Image, Size = 4 })];

    private static RemoteRegistryClient ClientFor(HttpMessageHandler handler, int concurrency = 3,
        int batchSize = TaskImportWrites.SaveBatch)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new RemoteRegistryClient(factory.Object,
            new ImportSettings { RegistryDownloadConcurrency = concurrency, RegistryDownloadMaxRetries = 1 },
            TestPaths.Policy, TestScratch.Provider, NullLogger<RemoteRegistryClient>.Instance)
        {
            PlaceBatchSize = batchSize
        };
    }

    private Task<TaskImportWrites> WritesAsync() =>
        TaskImportWrites.StartAsync(_recorder, Dataset, _dataset, CancellationToken.None);

    private string[] FilesInDataset() =>
    [
        .. Directory.EnumerateFiles(_dataset, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_dataset, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(IDDB.UploadsFolderName + "/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
    ];

    private string[] LeftInUploads() =>
        Directory.Exists(Path.Combine(_dataset, IDDB.UploadsFolderName))
            ? Directory.GetFiles(Path.Combine(_dataset, IDDB.UploadsFolderName))
            : [];

    [Test]
    public async Task EveryFile_IsRecordedBeforeItsMove_StampedFromItsTemporaryFile_AndTheNewFoldersToo()
    {
        var client = ClientFor(new RegistryHandler());
        var writes = await WritesAsync();
        var paths = new[] { "a.jpg", "b.jpg", "sub/c.jpg", "sub/deep/d.jpg" };

        // The destination is recorded before it exists
        _recorder.OnRecord = p => File.Exists(Path.Combine(_dataset, p)).ShouldBeFalse($"'{p}' is recorded before it is written");

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset, Entries(paths),
            writes, null, CancellationToken.None);

        FilesInDataset().ShouldBe(paths.Order(StringComparer.Ordinal));
        _recorder.Paths.Order(StringComparer.Ordinal).ShouldBe(paths.Order(StringComparer.Ordinal));
        writes.Created.Order(StringComparer.Ordinal).ShouldBe(paths.Order(StringComparer.Ordinal));
        _recorder.Folders.Order(StringComparer.Ordinal).ShouldBe(["sub", "sub/deep"]);
        _recorder.Stamped.Keys.Order(StringComparer.Ordinal).ShouldBe(paths.Order(StringComparer.Ordinal));
        _recorder.Stamped.Values.ShouldAllBe(f => f.Contains(IDDB.UploadsFolderName), "stamped before the move");
        LeftInUploads().ShouldBeEmpty("moved, not copied");
    }

    [Test]
    public async Task APathListedTwice_IsDownloadedAndMovedOnce_AndStaysInTheRecord()
    {
        var handler = new RegistryHandler();
        var client = ClientFor(handler);
        var writes = await WritesAsync();

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset,
            Entries("a.jpg", "b.jpg", "a.jpg"), writes, null, CancellationToken.None);

        FilesInDataset().ShouldBe(["a.jpg", "b.jpg"]);
        handler.Downloaded.Count(p => p == "a.jpg").ShouldBe(1, "the last entry wins, once");
        _recorder.Paths.Order(StringComparer.Ordinal).ShouldBe(["a.jpg", "b.jpg"]);
        _recorder.Forgotten.ShouldBeEmpty("a file the task moved is never dropped from its record");
        writes.Created.Order(StringComparer.Ordinal).ShouldBe(["a.jpg", "b.jpg"]);
        LeftInUploads().ShouldBeEmpty();
    }

    [Test]
    public async Task AFileThatIsNotTheTasks_IsKept_NotRecorded_AndTheOthersAreImported()
    {
        File.WriteAllText(Path.Combine(_dataset, "a.jpg"), "mine");
        var client = ClientFor(new RegistryHandler());
        var writes = await WritesAsync();

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset,
            Entries("a.jpg", "b.jpg"), writes, null, CancellationToken.None);

        File.ReadAllText(Path.Combine(_dataset, "a.jpg")).ShouldBe("mine");
        File.ReadAllText(Path.Combine(_dataset, "b.jpg")).ShouldBe("data");
        _recorder.Paths.ShouldBe(["b.jpg"]);
        writes.Created.ShouldBe(["b.jpg"]);
    }

    [Test]
    public async Task AFileTheTaskWroteBefore_AtTheExpectedSize_IsKept_AndStillOfTheTask()
    {
        File.WriteAllText(Path.Combine(_dataset, "a.jpg"), "done");
        _recorder.Earlier.Add("a.jpg");
        var handler = new RegistryHandler();
        var client = ClientFor(handler);
        var writes = await WritesAsync();

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset, Entries("a.jpg"),
            writes, null, CancellationToken.None);

        handler.Downloaded.ShouldBeEmpty("a complete file of the task is not downloaded again");
        writes.Created.ShouldBe(["a.jpg"]);
    }

    [Test]
    public async Task AFileTheTaskWroteBefore_AtAnotherSize_IsDownloadedAgain()
    {
        File.WriteAllText(Path.Combine(_dataset, "a.jpg"), "partially");
        _recorder.Earlier.Add("a.jpg");
        var client = ClientFor(new RegistryHandler());
        var writes = await WritesAsync();

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset, Entries("a.jpg"),
            writes, null, CancellationToken.None);

        File.ReadAllText(Path.Combine(_dataset, "a.jpg")).ShouldBe("data");
    }

    [Test]
    public async Task AFileThatAppearsWhileItIsDownloaded_IsNeverReplaced_AndIsForgotten()
    {
        var target = Path.Combine(_dataset, "a.jpg");
        var client = ClientFor(new RegistryHandler(onDownload: _ => File.WriteAllText(target, "somebody else's")));
        var writes = await WritesAsync();

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", _dataset, Entries("a.jpg"),
            writes, null, CancellationToken.None);

        File.ReadAllText(target).ShouldBe("somebody else's");
        _recorder.Paths.ShouldBeEmpty();
        writes.Created.ShouldBeEmpty();
        LeftInUploads().ShouldBeEmpty();
    }

    [Test]
    public async Task ACancel_WithSeveralDownloadsInFlight_IsACancellation_NotAnIOException_AndTheRecordCoversWhatWasWritten()
    {
        // The first files are served at once, the others stay in flight until the token is cancelled (a slow remote)
        // Batches of two: the first batch is moved into the dataset before the cancel stops the second
        var files = Enumerable.Range(0, 12).Select(i => $"f{i:00}.jpg").ToArray();
        var handler = new RegistryHandler(stallFrom: 3);
        var client = ClientFor(handler, concurrency: 4, batchSize: 2);
        var writes = await WritesAsync();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ImportProgress>(p =>
        {
            // The first batch is in the dataset: the user cancels (or the quota guard of the tool does)
            if (p.FilesDone >= 3)
                cts.Cancel();
        });

        var ex = await Should.ThrowAsync<Exception>(() => client.DownloadFilesParallelAsync("http://remote.test", null,
            "src", "ds", _dataset, Entries(files), writes, progress, cts.Token));

        ex.ShouldBeAssignableTo<OperationCanceledException>();
        FilesInDataset().ShouldNotBeEmpty("some files were already imported");
        FilesInDataset().Length.ShouldBeLessThan(files.Length);
        _recorder.Paths.ShouldBeSubsetOf(files);
        FilesInDataset().ShouldBeSubsetOf(_recorder.Paths, "every file the import wrote is in the record");
        LeftInUploads().ShouldBeEmpty("no temporary file is left behind");
    }

    [Test]
    public async Task ACancelBeforeTheFirstFile_WritesNothing()
    {
        var client = ClientFor(new RegistryHandler(stallFrom: 0));
        var writes = await WritesAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ex = await Should.ThrowAsync<Exception>(() => client.DownloadFilesParallelAsync("http://remote.test", null,
            "src", "ds", _dataset, Entries("a.jpg", "b.jpg"), writes, null, cts.Token));

        ex.ShouldBeAssignableTo<OperationCanceledException>();
        FilesInDataset().ShouldBeEmpty();
        _recorder.Paths.ShouldBeEmpty();
        LeftInUploads().ShouldBeEmpty();
    }

    [Test]
    public async Task ARealFailure_IsAnIOException_NotACancellation()
    {
        var client = ClientFor(new RegistryHandler(status: HttpStatusCode.NotFound));
        var writes = await WritesAsync();

        await Should.ThrowAsync<IOException>(() => client.DownloadFilesParallelAsync("http://remote.test", null, "src",
            "ds", _dataset, Entries("a.jpg"), writes, null, CancellationToken.None));

        FilesInDataset().ShouldBeEmpty();
        LeftInUploads().ShouldBeEmpty();
    }

    [Test]
    public async Task AFailedFile_IsReportedAsTheFailure_NotTheDownloadsItCancelledInFlight()
    {
        // The other downloads are cancelled when bad.jpg fails; their cancellation is not an I/O error of their own
        var handler = new RegistryHandler(stallExcept: "bad.jpg", status: HttpStatusCode.NotFound);
        var client = ClientFor(handler, concurrency: 4);
        var writes = await WritesAsync();

        var ex = await Should.ThrowAsync<IOException>(() => client.DownloadFilesParallelAsync("http://remote.test", null,
            "src", "ds", _dataset, Entries("a.jpg", "b.jpg", "bad.jpg", "c.jpg"), writes, null, CancellationToken.None));

        ex.Message.ShouldContain("'bad.jpg'");
        ex.Message.ShouldNotContain("cancelled");
        LeftInUploads().ShouldBeEmpty();
    }

    // A remote registry: GET .../download?path=... serves a 4-byte body. From the file number stallFrom on (or for every
    // file but stallExcept), the answer never comes (until the request is cancelled)
    private sealed class RegistryHandler(int stallFrom = int.MaxValue, HttpStatusCode status = HttpStatusCode.OK,
        Action<string>? onDownload = null, string? stallExcept = null) : HttpMessageHandler
    {
        private int _served;

        public List<string> Downloaded { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(kv => kv.Split('=', 2)).Single(kv => kv[0] == "path")[1]);
            lock (Downloaded)
                Downloaded.Add(path);

            if (stallExcept != null && path != stallExcept)
                await Task.Delay(Timeout.Infinite, ct);
            if (Interlocked.Increment(ref _served) > stallFrom)
                await Task.Delay(Timeout.Infinite, ct);

            onDownload?.Invoke(path);
            return new HttpResponseMessage(status) { Content = new ByteArrayContent("data"u8.ToArray()) };
        }
    }

    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
