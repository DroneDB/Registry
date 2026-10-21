#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// REG-04: multi-file downloads are streamed to the response. The baseline (2026-10-13) built the
/// whole archive in a MemoryStream (up to 1 GB) before the first byte: 160 MB allocated for a 64 MB
/// selection, every file read twice before any output.
/// Run: dotnet test --filter "FullyQualifiedName~ZipDownloadMemoryBaselineTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class ZipDownloadMemoryBaselineTests : TestBase
{
    private const int Files = 8;
    private const int FileSize = 8 * 1024 * 1024;          // 64 MB selection

    private string _root = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private int _filesOpened;

    /// <summary>
    /// A response body as Kestrel gives it: no seeking, no synchronous writes. Records how many source
    /// files had been opened when the first byte arrived, and can cancel the download at that moment.
    /// </summary>
    private sealed class ResponseLikeStream(Func<int> opened, Action? onFirstWrite = null, MemoryStream? capture = null) : Stream
    {
        public long Written;
        public int OpenedAtFirstWrite = -1;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (OpenedAtFirstWrite < 0)
            {
                OpenedAtFirstWrite = opened();
                onFirstWrite?.Invoke();
            }
            Written += buffer.Length;
            capture?.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed");

        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed");
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "reg04-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, IDDB.DatabaseFolderName, IDDB.BuildFolderName));
        var rnd = new Random(42);
        for (var i = 0; i < Files; i++)
        {
            var bytes = new byte[FileSize];
            rnd.NextBytes(bytes);                                      // incompressible, like JPEGs
            File.WriteAllBytes(Path.Combine(_root, $"IMG_{i}.jpg"), bytes);
        }
        File.WriteAllText(Path.Combine(_root, IDDB.DatabaseFolderName, IDDB.DatabaseFileName), "index");

        _filesOpened = 0;
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_root);
        ddb.SetupGet(d => d.BuildFolderPath).Returns(Path.Combine(_root, IDDB.DatabaseFolderName, IDDB.BuildFolderName));
        ddb.Setup(d => d.Checkpoint()).Returns(true);
        ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns((string p) =>
        {
            _filesOpened++;
            return Path.Combine(_root, p);
        });
        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private FileStreamDescriptor CreateDescriptor(FileDescriptorType type = FileDescriptorType.Multiple) =>
        new("sel.zip", "application/zip", "org", Guid.NewGuid(),
            Enumerable.Range(0, Files).Select(i => $"IMG_{i}.jpg").ToArray(), [],
            type, CreateTestLogger<ObjectsManager>(Serilog.Events.LogEventLevel.Warning),
            _ddbManager.Object, new ZipArchiveBuilder());

    [Test]
    public async Task Target_64MBSelection_StreamsWithBoundedMemory()
    {
        var output = new ResponseLikeStream(() => _filesOpened);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        await CreateDescriptor().CopyToAsync(output);

        var allocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - before) / (1024 * 1024);
        TestContext.WriteLine($"{Files} x {FileSize / (1024 * 1024)} MB -> zip {output.Written / (1024 * 1024)} MB; " +
                              $"managed allocations {allocatedMb} MB; files opened before the first response byte: {output.OpenedAtFirstWrite}");
        output.Written.ShouldBeGreaterThan((long)Files * FileSize);
        allocatedMb.ShouldBeLessThanOrEqualTo(16, "the archive must be streamed, not buffered");
        output.OpenedAtFirstWrite.ShouldBeLessThan(Files, "the first bytes must leave while files are still being read");
    }

    [Test]
    public async Task WholeDataset_IsWrittenWithoutSynchronousWrites()
    {
        // The index folder goes in too (CreateEntryFromAnyAsync): a synchronous write would throw here
        var output = new ResponseLikeStream(() => _filesOpened);
        await CreateDescriptor(FileDescriptorType.Dataset).CopyToAsync(output);
        output.Written.ShouldBeGreaterThan((long)Files * FileSize);
    }

    [Test]
    public async Task StreamedArchive_IsAValidZipWithEveryFile()
    {
        using var bytes = new MemoryStream();
        await CreateDescriptor(FileDescriptorType.Dataset).CopyToAsync(new ResponseLikeStream(() => _filesOpened, capture: bytes));

        bytes.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        names.Count(n => n.StartsWith("IMG_")).ShouldBe(Files);
        names.ShouldContain(IDDB.DatabaseFolderName + "/" + IDDB.DatabaseFileName);

        await using var entry = await archive.GetEntry("IMG_3.jpg")!.OpenAsync();
        using var copy = new MemoryStream();
        await entry.CopyToAsync(copy);
        copy.ToArray().ShouldBe(await File.ReadAllBytesAsync(Path.Combine(_root, "IMG_3.jpg")));
    }

    [Test]
    public async Task ClientAbort_StopsReadingFiles()
    {
        using var aborted = new CancellationTokenSource();
        var output = new ResponseLikeStream(() => _filesOpened, aborted.Cancel);

        await Should.ThrowAsync<OperationCanceledException>(() => CreateDescriptor().CopyToAsync(output, aborted.Token));

        _filesOpened.ShouldBeLessThan(Files, "a download whose client went away must stop reading the dataset");
    }
}
