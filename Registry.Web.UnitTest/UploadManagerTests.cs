#nullable enable
using Registry.Web.Services.Jobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// UPL-01 upload pipeline: bundles (C.2), pre-checks before the first byte (E.2), receipts for a
/// lost response (E.4), and the dataset lookup cache with its EF hook (REG-05 item 3).
/// </summary>
[TestFixture]
public class UploadManagerTests
{
    private const string Org = "org";
    private const string Ds = "ds";

    private string _root = null!;
    private Dataset _dataset = null!;
    private Mock<IUtils> _utils = null!;
    private Mock<IAuthManager> _auth = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IDatasetIndexQueue> _queue = null!;
    private Mock<IDatasetChangeCoalescer> _changes = null!;
    private IUploadReceiptStore _receipts = null!;
    private AppSettings _settings = null!;
    private int _enqueues, _evaluations;
    private long _used, _limit = long.MaxValue;

    [SetUp]
    public void SetUp()
    {
        _enqueues = _evaluations = 0;
        _used = 0;
        _limit = long.MaxValue;
        _root = Path.Combine(Path.GetTempPath(), "upl-mgr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), Organization = new Organization { Slug = Org } };
        _settings = new AppSettings
        {
            DatasetsPath = _root,
            Import = new ImportSettings { BlockedFileExtensions = [".exe"] }
        };

        _utils = new Mock<IUtils>();
        _utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_dataset);
        _utils.Setup(u => u.GetCurrentUserStorageLimit()).Returns(() =>
        {
            Interlocked.Increment(ref _evaluations);
            return Task.FromResult(new UserStorageInfo { Used = _used, Total = _limit });
        });

        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), AccessType.Write)).ReturnsAsync(true);
        _auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = "u1" });

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(_root);
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(_root, p));

        _queue = new Mock<IDatasetIndexQueue>();
        _queue.Setup(q => q.BeginUpload(It.IsAny<DatasetKey>())).Returns(() => new TestTicket());
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<AddItem>>(),
                It.IsAny<UploadTicket?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DatasetKey _, IReadOnlyList<AddItem> items, UploadTicket? ticket, CancellationToken _) =>
            {
                Interlocked.Increment(ref _enqueues);
                ticket?.End();
                return items.Select(i => i.Path.Contains("corrupt")
                    ? new IndexOutcome(null, new DdbException("[GDAL] corrupt"))
                    : new IndexOutcome(new Entry { Path = i.Path, Hash = i.Hint?.Sha256, Type = EntryType.Generic }, null)).ToArray();
            });

        _changes = new Mock<IDatasetChangeCoalescer>();
        _receipts = new DistributedUploadReceiptStore(
            new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            TestSettings.Options<UploadSettings>(Microsoft.Extensions.Options.Options.Create(_settings)));
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private sealed class TestTicket : UploadTicket
    {
        protected override void OnEnded() { }
    }

    private UploadManager CreateManager()
    {
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, _dataset.InternalRef)).Returns(_ddb.Object);
        var options = Microsoft.Extensions.Options.Options.Create(_settings);
        var context = TestDb.CreateContext<RegistryContext>();
        var jobs = Mock.Of<IBackgroundJobsProcessor>();
        var buildPending = new BuildPendingService(context, new DatasetSweep(context), ddbManager.Object, jobs, Mock.Of<ICacheManager>(), TestJobs.NewStorage(), NullLogger<BuildPendingService>.Instance);

        return new UploadManager(NullLogger<UploadManager>.Instance, _auth.Object,
            new DatasetAccessResolver(_utils.Object, _auth.Object, ddbManager.Object, TestPaths.Policy), ddbManager.Object,
            new Registry.Adapters.FileSystem(), TestSettings.Options<ThumbnailOptions>(options), TestSettings.Options<ImportSettings>(options), jobs, buildPending,
            new UploadQuotaGuard(new UploadQuotaLedger(TimeSpan.FromMinutes(1)), _utils.Object), _changes.Object,
            TestPaths.Policy,
            TestScratch.Provider,
            _receipts, _queue.Object);
    }

    private static async Task<IReadOnlyList<StreamedPart>> Stream(IUploadManager uploads,
        Services.Uploads.UploadRequestContext ctx, params string[] paths)
    {
        var parts = new List<StreamedPart>();
        foreach (var path in paths)
            parts.Add(new StreamedPart(path, await uploads.StreamAsync(ctx, new MemoryStream(Encoding.UTF8.GetBytes(path)))));
        return parts;
    }

    [Test]
    public async Task Bundle_IsOneQuotaDecision_OneIndexBatch_AndOneInvalidation()
    {
        var uploads = CreateManager();
        using var ctx = await uploads.BeginAsync(Org, Ds, 4096);

        var results = await uploads.CommitAsync(ctx, await Stream(uploads, ctx, "a/1.txt", "a/2.txt", "a/3.txt"));

        results.Select(r => r.Status).ShouldBe([201, 201, 201]);
        results.Select(r => r.Entry!.Path).ShouldBe(["a/1.txt", "a/2.txt", "a/3.txt"]);
        File.Exists(Path.Combine(_root, "a/2.txt")).ShouldBeTrue();
        _enqueues.ShouldBe(1);
        _evaluations.ShouldBe(1);
        _changes.Verify(c => c.FilesAdded(Org, Ds, It.Is<IEnumerable<string>>(p => p.Count() == 3)), Times.Once);
    }

    [Test]
    public async Task Bundle_ReportsEachFile_AndAFailureFailsOnlyItsFile()
    {
        var uploads = CreateManager();
        using var ctx = await uploads.BeginAsync(Org, Ds);

        var results = await uploads.CommitAsync(ctx,
            await Stream(uploads, ctx, "ok.txt", "tool.exe", ".ddb/evil.txt", "corrupt.tif"));

        results[0].Status.ShouldBe(StatusCodes.Status201Created);
        results[1].Status.ShouldBe(StatusCodes.Status400BadRequest);           // blocked extension
        results[2].Status.ShouldBe(StatusCodes.Status400BadRequest);           // reserved path (RQ-10: was 500)
        results[3].Error.ShouldNotBeNull();                                    // index failure
        File.Exists(Path.Combine(_root, "ok.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "tool.exe")).ShouldBeFalse();
        // The file that failed to index is quarantined, not left untracked
        File.Exists(Path.Combine(_root, "corrupt.tif")).ShouldBeFalse();
        Directory.GetFiles(Path.Combine(_root, IDDB.UploadsFolderName, IDDB.QuarantineFolderName)).Length.ShouldBe(1);
        // No temp file is left behind
        Directory.GetFiles(Path.Combine(_root, IDDB.UploadsFolderName), "*.tmp").ShouldBeEmpty();
    }

    [Test]
    public async Task HashComputedWhileStreaming_IsTheIndexHint()
    {
        var uploads = CreateManager();
        IReadOnlyList<AddItem>? seen = null;
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<AddItem>>(),
                It.IsAny<UploadTicket?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DatasetKey _, IReadOnlyList<AddItem> items, UploadTicket? _, CancellationToken _) =>
            {
                seen = items;
                return items.Select(i => new IndexOutcome(new Entry { Path = i.Path, Hash = "x" }, null)).ToArray();
            });
        using var ctx = await uploads.BeginAsync(Org, Ds);

        var file = await uploads.StreamAsync(ctx, new MemoryStream("payload"u8.ToArray()));
        await uploads.CommitAsync(ctx, "p.bin", file);

        seen!.Single().Hint!.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("payload"u8)));
        seen!.Single().Hint!.Size.ShouldBe(7);
    }

    [Test]
    public async Task OverQuota_IsRefusedBeforeTheFirstByte()
    {
        _used = 90;
        _limit = 100;
        var uploads = CreateManager();

        await Should.ThrowAsync<QuotaExceededException>(() => uploads.BeginAsync(Org, Ds, expectedBytes: 11));
        Directory.Exists(Path.Combine(_root, IDDB.UploadsFolderName)).ShouldBeFalse("nothing was written");
    }

    [Test]
    public async Task WithoutWriteAccess_IsRefusedBeforeTheFirstByte()
    {
        _auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), AccessType.Write)).ReturnsAsync(false);

        await Should.ThrowAsync<ForbiddenException>(() => CreateManager().BeginAsync(Org, Ds, 10));
    }

    [Test]
    public async Task ValidatePath_RefusesABlockedDestinationBeforeStreaming()
    {
        var uploads = CreateManager();
        using var ctx = await uploads.BeginAsync(Org, Ds);

        Should.Throw<ExtensionBlockedException>(() => uploads.ValidatePath(ctx, "setup.exe"));
        // RQ-10: one rule (libddb's) and one 400 for every refused path; a reserved path was a 500
        Should.Throw<InvalidDatasetPathException>(() => uploads.ValidatePath(ctx, ".uploads/x.bin"))
            .Rejection.ShouldBe(EntryPathRejection.Reserved);
        Should.Throw<InvalidDatasetPathException>(() => uploads.ValidatePath(ctx, "../outside.txt"))
            .Rejection.ShouldBe(EntryPathRejection.Traversal);
        // Absolute on every OS (libddb's rule); Path.IsPathRooted accepted these on Linux
        Should.Throw<InvalidDatasetPathException>(() => uploads.ValidatePath(ctx, @"C:\evil.txt"))
            .Rejection.ShouldBe(EntryPathRejection.Absolute);
        Should.Throw<InvalidDatasetPathException>(() => uploads.ValidatePath(ctx, @"\\server\share\evil.txt"))
            .Rejection.ShouldBe(EntryPathRejection.Absolute);
        Should.Throw<InvalidDatasetPathException>(() => uploads.ValidatePath(ctx, "a\0.txt"))
            .Rejection.ShouldBe(EntryPathRejection.Nul);
        Should.Throw<InvalidDatasetPathException>(() => uploads.ValidatePath(ctx, "sub/.ddb/x.txt"))
            .Rejection.ShouldBe(EntryPathRejection.Reserved);
        uploads.ValidatePath(ctx, "photos/ok.jpg").ShouldBe("photos/ok.jpg");
        uploads.ValidatePath(ctx, "./photos//ok.jpg").ShouldBe("photos/ok.jpg");
    }

    [Test]
    public async Task Receipt_AnswersARepeatedUpload_WithoutAddingItAgain()
    {
        var uploads = CreateManager();
        const string id = "6f0c3b1e-4a5d-4c3e-9f0a-1b2c3d4e5f60";

        using (var ctx = await uploads.BeginAsync(Org, Ds, 10, id))
        {
            (await uploads.GetReceiptAsync(Org, Ds, id))!.State.ShouldBe(UploadReceiptState.Receiving);
            var file = await uploads.StreamAsync(ctx, new MemoryStream("one"u8.ToArray()));
            await uploads.CommitAsync(ctx, "one.txt", file);
        }

        var receipt = await uploads.GetReceiptAsync(Org, Ds, id);
        receipt!.State.ShouldBe(UploadReceiptState.Done);
        receipt.Results!.Single().Entry!.Path.ShouldBe("one.txt");

        // The client lost the 201 (e.g. a proxy 504) and sends the same upload again
        using var again = await uploads.BeginAsync(Org, Ds, 10, id);
        again.Receipt!.Results!.Single().Entry!.Path.ShouldBe("one.txt");
        _enqueues.ShouldBe(1);
    }

    [Test]
    public async Task Receipt_UploadInProgress_IsAConflict_AndAFailureIsRecorded()
    {
        var uploads = CreateManager();
        const string id = "upload-in-progress-0001";

        using (var ctx = await uploads.BeginAsync(Org, Ds, 10, id))
        {
            await Should.ThrowAsync<ConflictException>(() => uploads.BeginAsync(Org, Ds, 10, id));

            var file = await uploads.StreamAsync(ctx, new MemoryStream("x"u8.ToArray()));
            await Should.ThrowAsync<ExtensionBlockedException>(() => uploads.CommitAsync(ctx, "bad.exe", file));
        }

        var receipt = await uploads.GetReceiptAsync(Org, Ds, id);
        receipt!.State.ShouldBe(UploadReceiptState.Failed);
        receipt.Status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Receipt_ClientThatWentAway_IsForgotten_SoTheUploadCanStartAgain()
    {
        var uploads = CreateManager();
        const string id = "aborted-upload-0001";

        using (var ctx = await uploads.BeginAsync(Org, Ds, 10, id))
            await uploads.FailAsync(ctx, new IOException("Unexpected end of request content"));

        (await uploads.GetReceiptAsync(Org, Ds, id)).ShouldBeNull();
        using var again = await uploads.BeginAsync(Org, Ds, 10, id);
        again.Receipt.ShouldBeNull();
    }

    [Test]
    public async Task InvalidUploadId_IsRefused()
    {
        await Should.ThrowAsync<BadRequestException>(() => CreateManager().BeginAsync(Org, Ds, 10, "a b"));
    }

    [Test]
    public async Task DisposedContext_DeletesTempFilesThatWereNeverCommitted()
    {
        var uploads = CreateManager();
        string temp;
        using (var ctx = await uploads.BeginAsync(Org, Ds))
        {
            temp = (await uploads.StreamAsync(ctx, new MemoryStream("orphan"u8.ToArray()))).TempPath;
            File.Exists(temp).ShouldBeTrue();
        }

        File.Exists(temp).ShouldBeFalse();
    }

    // ---- resumable chunked uploads (E.5)

    private ChunkedUploadManager CreateChunks(UploadManager uploads) =>
        new(uploads, new ChunkStore(), new UploadQuotaGuard(new UploadQuotaLedger(TimeSpan.FromMinutes(1)), _utils.Object),
            TestScratch.Provider);

    private static ChunkRequest Chunk(int index, string path = "big.bin") => new("big-file-0001", index, 3, 25, 10, path);

    private static MemoryStream Body(int index) => new(new byte[index == 2 ? 5 : 10]);

    [Test]
    public async Task Chunked_CommitIsIdempotent_AndCarriesTheHash()
    {
        var uploads = CreateManager();
        var chunks = CreateChunks(uploads);
        string? hinted = null;
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<AddItem>>(),
                It.IsAny<UploadTicket?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DatasetKey _, IReadOnlyList<AddItem> items, UploadTicket? _, CancellationToken _) =>
            {
                Interlocked.Increment(ref _enqueues);
                hinted = items[0].Hint!.Sha256;
                return [new IndexOutcome(new Entry { Path = items[0].Path, Hash = hinted }, null)];
            });

        foreach (var i in new[] { 2, 0, 1 })
            await chunks.WriteChunkAsync(Org, Ds, Chunk(i), Body(i));

        var entry = await chunks.CommitAsync(Org, Ds, "big-file-0001");
        entry.Path.ShouldBe("big.bin");
        hinted.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[25])));
        File.ReadAllBytes(Path.Combine(_root, "big.bin")).Length.ShouldBe(25);

        // The client lost the response and commits again: same entry, not indexed twice
        (await chunks.CommitAsync(Org, Ds, "big-file-0001")).Path.ShouldBe("big.bin");
        _enqueues.ShouldBe(1);
    }

    [Test]
    public async Task Chunked_CommitWithMissingChunks_IsAConflict_AndCanBeRetried()
    {
        var uploads = CreateManager();
        var chunks = CreateChunks(uploads);
        await chunks.WriteChunkAsync(Org, Ds, Chunk(0), Body(0));

        (await chunks.GetStatusAsync(Org, Ds, "big-file-0001")).Missing.ShouldBe([1, 2]);
        await Should.ThrowAsync<ConflictException>(() => chunks.CommitAsync(Org, Ds, "big-file-0001"));

        await chunks.WriteChunkAsync(Org, Ds, Chunk(1), Body(1));
        await chunks.WriteChunkAsync(Org, Ds, Chunk(2), Body(2));
        (await chunks.CommitAsync(Org, Ds, "big-file-0001")).Path.ShouldBe("big.bin");
    }

    [Test]
    public async Task Chunked_FirstChunkChecksQuotaAndPath_BeforeStoringAnything()
    {
        _used = 90;
        _limit = 100;
        var chunks = CreateChunks(CreateManager());

        await Should.ThrowAsync<QuotaExceededException>(() => chunks.WriteChunkAsync(Org, Ds, Chunk(0), Body(0)));
        await Should.ThrowAsync<ExtensionBlockedException>(() => chunks.WriteChunkAsync(Org, Ds, Chunk(0, "x.exe"), Body(0)));
        Directory.Exists(Path.Combine(_root, IDDB.UploadsFolderName, "chunks", "big-file-0001")).ShouldBeFalse();
    }

    // ---- REG-05 item 3: dataset lookups

    [Test]
    public async Task GetDataset_IsServedFromTheCache_UntilTheRowChanges()
    {
        var lookups = new DatasetLookupCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var counter = new QueryCounter();
        await using var db = TestDb.Create<RegistryContext>(b =>
            b.AddInterceptors(new DatasetChangeInterceptor(lookups), counter));

        await using (var seed = db.NewContext())
        {
            var org = new Organization { Slug = "acme", Name = "Acme", OwnerId = "u1", CreationDate = DateTime.UtcNow };
            seed.Organizations.Add(org);
            seed.Datasets.Add(new Dataset { Slug = "survey", InternalRef = Guid.NewGuid(), Organization = org, CreationDate = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var context = db.NewContext();
        var utils = new WebUtils(Mock.Of<IAuthManager>(), context, Microsoft.Extensions.Options.Options.Create(new AccountOptions()),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions()),
            Mock.Of<IHttpContextAccessor>(), Mock.Of<IDatasetSummaryProvider>(), lookups);

        counter.Reset();
        (await utils.GetDatasetAsync("acme", "survey")).Slug.ShouldBe("survey");
        await utils.GetDatasetAsync("acme", "survey");
        await utils.GetDatasetAsync("acme", "survey");
        counter.Queries.ShouldBe(1);

        // A rename through EF drops the cached lookup: the old slug is gone at once
        var tracked = await utils.GetDatasetAsync("acme", "survey", withTracking: true);
        tracked.Slug = "survey-2";
        await context.SaveChangesAsync();

        (await utils.GetDatasetAsync("acme", "survey", safe: true)).ShouldBeNull();
        (await utils.GetDatasetAsync("acme", "survey-2")).ShouldNotBeNull();
    }

    private sealed class QueryCounter : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
    {
        public int Queries { get; private set; }
        public void Reset() => Queries = 0;

        public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData data, object entity)
        {
            if (entity is Dataset) Queries++;
            return entity;
        }
    }
}
