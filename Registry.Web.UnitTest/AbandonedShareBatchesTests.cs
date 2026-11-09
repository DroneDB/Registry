using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;
using Entry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Test;

/// <summary>
/// The recurring job that rolls back the abandoned /upload batches (FR-UPL-01, §8.3, D-049, AC-024): a batch with no
/// upload activity (chunk or file) for 24 hours ends rolled back, with the dataset it created while that dataset holds
/// only the batch's files; a batch on an existing dataset loses only the files it created (a replaced file stays,
/// FR-XC-27); a commit that comes first wins. The manual rollback follows the same rules.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AbandonedShareBatchesTests"
/// </summary>
[TestFixture]
public class AbandonedShareBatchesTests
{
    private const string Org = "alice";

    private TestDb<RegistryContext> _db = null!;
    private RegistryContext _context = null!;
    private FakeTime _time = null!;
    private string _root = null!;
    private readonly Dictionary<Guid, FakeDataset> _fakes = [];
    private Mock<IDatasetListingInvalidator> _listings = null!;
    private CommitBeforeClose _interceptor = null!;
    private int _batchNumber;

    /// <summary>The index and the folder of one dataset.</summary>
    private sealed class FakeDataset
    {
        public required Mock<IDDB> Ddb { get; init; }
        public required Dictionary<string, EntryType> Indexed { get; init; }
        public required string Root { get; init; }
    }

    [SetUp]
    public void SetUp()
    {
        _interceptor = new CommitBeforeClose();
        _db = TestDb.Create<RegistryContext>(o => o.AddInterceptors(_interceptor));
        _context = _db.NewContext();
        _context.Organizations.Add(new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow, Datasets = [] });
        _context.SaveChanges();

        _time = new FakeTime(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        _root = Path.Combine(Path.GetTempPath(), "abandoned-batches-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _fakes.Clear();
        _listings = new Mock<IDatasetListingInvalidator>();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private FakeDataset Fake(Dataset ds) => Fake(ds.InternalRef);

    private FakeDataset Fake(Guid internalRef)
    {
        if (_fakes.TryGetValue(internalRef, out var fake))
            return fake;

        var root = Path.Combine(_root, internalRef.ToString("N"));
        Directory.CreateDirectory(root);
        var indexed = new Dictionary<string, EntryType>(StringComparer.Ordinal);
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(root);
        ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(root, p));
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns<string>(indexed.ContainsKey);
        ddb.Setup(d => d.Remove(It.IsAny<string>())).Callback<string>(p => indexed.Remove(p));
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns(() =>
            indexed.Select(kv => new EntrySummary { Path = kv.Key, Type = kv.Value }).ToList());
        return _fakes[internalRef] = new FakeDataset { Ddb = ddb, Indexed = indexed, Root = root };
    }

    private string Local(Dataset ds, string path) => Path.Combine(Fake(ds).Root, path);

    // A file that is in the dataset (indexed and on disk), not through a batch
    private void PutFile(Dataset ds, string path, EntryType type = EntryType.Generic)
    {
        if (type != EntryType.Directory)
        {
            var local = Local(ds, path);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, path);
        }

        Fake(ds).Indexed[path] = type;
    }

    private Dataset AddDataset(string slug)
    {
        var ds = new Dataset
        {
            Slug = slug, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow,
            Organization = _context.Organizations.Single(o => o.Slug == Org)
        };
        _context.Datasets.Add(ds);
        _context.SaveChanges();
        Fake(ds);
        return ds;
    }

    // A running batch that started `age` ago; `uploads` are the ages of its files; `replaced` are paths that were in
    // the dataset before the batch wrote them; `preUpgrade`: a batch from before the BatchCreatedDataset migration (no
    // last activity, CreatedDataset and Replaced false as the migration leaves them)
    private string AddBatch(Dataset ds, TimeSpan age, bool createdDataset, BatchStatus status = BatchStatus.Running,
        string[]? replaced = null, bool preUpgrade = false, params (string Path, TimeSpan Age)[] uploads)
    {
        var token = $"batch-{++_batchNumber}";
        var newest = uploads.Length == 0 ? age : TimeSpan.FromTicks(Math.Min(age.Ticks, uploads.Min(u => u.Age).Ticks));
        var batch = new Batch
        {
            Token = token, Dataset = _context.Datasets.Single(d => d.Id == ds.Id), UserName = "alice", Start = _time.Now - age, Status = status,
            CreatedDataset = createdDataset, LastActivity = preUpgrade ? null : _time.Now - newest, Entries = []
        };
        foreach (var (path, uploadAge) in uploads)
        {
            batch.Entries.Add(new Entry
            {
                Path = path, Hash = "h", Size = 1, Type = EntryType.Generic, AddedOn = _time.Now - uploadAge, Batch = batch,
                Replaced = replaced?.Contains(path) ?? false
            });
            PutFile(ds, path);
        }

        _context.Batches.Add(batch);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return token;
    }

    private IDdbManager DdbManager()
    {
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns<string, Guid>((_, r) => Fake(r).Ddb.Object);
        return ddbManager.Object;
    }

    private AbandonedShareBatchesService Service() =>
        new(_context,
            new DatasetRemoval(_context, _listings.Object, Mock.Of<IBackgroundJobsProcessor>(),
                NullLogger<DatasetRemoval>.Instance),
            DdbManager(),
            new TaskEntryChanges(Mock.Of<IDatasetCacheInvalidator>(), _context, NullLogger<TaskEntryChanges>.Instance),
            NullLogger<AbandonedShareBatchesService>.Instance, _time);

    private Task Run() => Service().RollbackAbandonedAsync(null, CancellationToken.None);

    private ShareBatchStore Store(RegistryContext? context = null)
    {
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync("alice");
        auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        return new ShareBatchStore(context ?? _context, auth.Object, Mock.Of<IDatasetAccessResolver>(),
            Mock.Of<IBatchTokenGenerator>(), _time, NullLogger<ShareBatchStore>.Instance);
    }

    // Null: the batch is gone, deleted (cascade) with the dataset it created
    private BatchStatus? StatusOf(string token)
    {
        _context.ChangeTracker.Clear();
        return _context.Batches.AsNoTracking().Where(b => b.Token == token).Select(b => (BatchStatus?)b.Status)
            .SingleOrDefault();
    }

    private bool DatasetExists(string slug)
    {
        _context.ChangeTracker.Clear();
        return _context.Datasets.AsNoTracking().Any(d => d.Slug == slug);
    }

    [Test]
    public void TheJob_IsRegisteredAsARecurringJob_EveryHour()
    {
        RecurringJobIds.Owned.ShouldContain("rollback-abandoned-share-batches");
        AbandonedShareBatchesService.AbandonedAfter.ShouldBe(TimeSpan.FromHours(24));
        AbandonedShareBatchesService.Cron.ShouldBe("0 * * * *");
    }

    [Test]
    public async Task ABatchWithNoUploadFor24Hours_IsRolledBack_AndItsCreatedDatasetIsDeleted()
    {
        var ds = AddDataset("half-filled");
        var token = AddBatch(ds, TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1), createdDataset: true,
            uploads: ("note.txt", TimeSpan.FromHours(25)));

        await Run();

        StatusOf(token).ShouldBeNull("the batch goes with the dataset it created");
        DatasetExists("half-filled").ShouldBeFalse("AC-024: no half-filled dataset remains");
        _listings.Verify(l => l.ListingChangedAsync(Org, ds.InternalRef, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ABatchThatMovedTheClock24Hours_IsRolledBack_AndOneBefore_IsNot()
    {
        var ds = AddDataset("clock");
        var token = AddBatch(ds, TimeSpan.FromHours(23), createdDataset: true);

        await Run();
        StatusOf(token).ShouldBe(BatchStatus.Running);
        DatasetExists("clock").ShouldBeTrue();

        _time.Advance(TimeSpan.FromHours(2));
        await Run();

        StatusOf(token).ShouldBeNull();
        DatasetExists("clock").ShouldBeFalse();
    }

    [Test]
    public async Task ABatchWhoseLastUploadIsRecent_IsKept_EvenWhenItStartedLongAgo()
    {
        var ds = AddDataset("busy");
        var token = AddBatch(ds, TimeSpan.FromHours(30), createdDataset: true,
            uploads: [("old.txt", TimeSpan.FromHours(30)), ("recent.txt", TimeSpan.FromHours(2))]);

        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Running);
        DatasetExists("busy").ShouldBeTrue();
    }

    [Test]
    public async Task ALargeFileWhoseChunksKeepArrivingForMoreThan24Hours_IsNotRolledBack_ThenIsOnceTheyStop()
    {
        // One file of 10 chunks, no file complete: a chunk every 5 hours for 30 hours (the batch started at 12:00)
        var ds = AddDataset("slow-link");
        var token = AddBatch(ds, TimeSpan.Zero, createdDataset: true);
        var uploads = new ShareChunkedUploads(Store(), Mock.Of<IStorageQuota>(), new ChunkStore(), TestScratch.Provider,
            DdbManager(), Mock.Of<IObjectsManager>(), TestPaths.Policy, NullLogger<ShareChunkedUploads>.Instance);

        for (var chunk = 0; chunk < 6; chunk++)
        {
            _time.Advance(TimeSpan.FromHours(5));
            await using var body = new MemoryStream("abcd"u8.ToArray());
            var result = await uploads.UploadChunk(token, new ChunkUploadDto
            {
                FileId = "big-file", Path = "orthophoto.tif", FileName = "orthophoto.tif", ChunkIndex = chunk,
                TotalChunks = 10, TotalFileSize = 40, ChunkSize = 4
            }, body);
            result.IsComplete.ShouldBeFalse();
            _context.ChangeTracker.Clear();

            await Run();

            StatusOf(token).ShouldBe(BatchStatus.Running, $"a chunk arrived {chunk} at +{(chunk + 1) * 5} h");
            DatasetExists("slow-link").ShouldBeTrue();
        }

        // 30 hours after the start, 24 hours after the last chunk the batch is still alive...
        _time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        await Run();
        StatusOf(token).ShouldBe(BatchStatus.Running);

        // ...and once the chunks stopped for more than 24 hours it is abandoned
        _time.Advance(TimeSpan.FromMinutes(2));
        await Run();
        StatusOf(token).ShouldBeNull();
        DatasetExists("slow-link").ShouldBeFalse();
    }

    [Test]
    public async Task ACommittedOrRolledBackBatch_IsLeftAlone()
    {
        var ds = AddDataset("done");
        var committed = AddBatch(ds, TimeSpan.FromDays(3), createdDataset: true, BatchStatus.Committed);

        await Run();

        StatusOf(committed).ShouldBe(BatchStatus.Committed);
        DatasetExists("done").ShouldBeTrue();
    }

    [Test]
    public async Task ACommitThatLandsJustBeforeTheJobClosesTheBatch_Wins_AndTheJobDoesNothing()
    {
        var ds = AddDataset("committed-late");
        var token = AddBatch(ds, TimeSpan.FromHours(30), createdDataset: true, uploads: ("a.jpg", TimeSpan.FromHours(29)));
        // The commit runs on the database right before the job's statement that closes the batch
        _interceptor.Arm(token);

        await Run();

        _interceptor.Fired.ShouldBeTrue();
        StatusOf(token).ShouldBe(BatchStatus.Committed);
        DatasetExists("committed-late").ShouldBeTrue();
        File.Exists(Local(ds, "a.jpg")).ShouldBeTrue();
        Fake(ds).Indexed.Keys.ShouldBe(["a.jpg"]);
    }

    [Test]
    public async Task ACreatedDatasetThatHoldsEntriesAddedOutsideTheBatch_IsKept_OnlyTheBatchFilesGo()
    {
        var ds = AddDataset("shared-later");
        var token = AddBatch(ds, TimeSpan.FromHours(48), createdDataset: true,
            uploads: [("sub/a.jpg", TimeSpan.FromHours(47)), ("b.jpg", TimeSpan.FromHours(46))]);
        PutFile(ds, "sub", EntryType.Directory);
        // Another upload added a file to the new dataset meanwhile
        PutFile(ds, "colleague.jpg");

        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        DatasetExists("shared-later").ShouldBeTrue("it holds a file that is not the batch's");
        File.Exists(Local(ds, "sub/a.jpg")).ShouldBeFalse();
        File.Exists(Local(ds, "b.jpg")).ShouldBeFalse();
        File.Exists(Local(ds, "colleague.jpg")).ShouldBeTrue();
        Fake(ds).Indexed.Keys.Order().ShouldBe(["colleague.jpg", "sub"]);
    }

    [Test]
    public async Task ACreatedDatasetThatHoldsOnlyTheBatchFilesAndTheirFolders_IsDeleted()
    {
        var ds = AddDataset("only-batch");
        var token = AddBatch(ds, TimeSpan.FromHours(48), createdDataset: true,
            uploads: [("day 1/sub/a.jpg", TimeSpan.FromHours(47))]);
        PutFile(ds, "day 1", EntryType.Directory);
        PutFile(ds, "day 1/sub", EntryType.Directory);

        await Run();

        StatusOf(token).ShouldBeNull();
        DatasetExists("only-batch").ShouldBeFalse();
    }

    [Test]
    public async Task ABatchOnAnExistingDataset_LosesItsFilesOnly_AndTheDatasetStays()
    {
        // `ddb share org/existing` that was never committed
        var ds = AddDataset("existing");
        PutFile(ds, "kept.jpg");
        var token = AddBatch(ds, TimeSpan.FromHours(48), createdDataset: false,
            uploads: [("sub/a.jpg", TimeSpan.FromHours(47)), ("b.jpg", TimeSpan.FromHours(46))]);

        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        DatasetExists("existing").ShouldBeTrue();
        File.Exists(Local(ds, "sub/a.jpg")).ShouldBeFalse();
        File.Exists(Local(ds, "b.jpg")).ShouldBeFalse();
        File.Exists(Local(ds, "kept.jpg")).ShouldBeTrue("a file that is not the batch's stays");
        Fake(ds).Indexed.Keys.ShouldBe(["kept.jpg"]);
    }

    [Test]
    public async Task AFileThatTheBatchReplaced_StaysAfterTheRollback()
    {
        var ds = AddDataset("existing");
        var token = AddBatch(ds, TimeSpan.FromHours(48), createdDataset: false, replaced: ["map.tif"],
            uploads: [("map.tif", TimeSpan.FromHours(47)), ("new.jpg", TimeSpan.FromHours(46))]);

        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        File.Exists(Local(ds, "map.tif")).ShouldBeTrue("FR-XC-27: a file that the batch replaces stays");
        File.Exists(Local(ds, "new.jpg")).ShouldBeFalse();
        Fake(ds).Indexed.Keys.ShouldBe(["map.tif"]);
    }

    [Test]
    public async Task AShareUpload_RecordsWhetherItReplacedAFile()
    {
        var ds = AddDataset("existing");
        PutFile(ds, "map.tif");
        var token = AddBatch(ds, TimeSpan.Zero, createdDataset: false);
        var objects = new Mock<IObjectsManager>();
        objects.Setup(o => o.CommitStreamedAsync(Org, "existing", It.IsAny<string>(), It.IsAny<StreamedFile>()))
            .ReturnsAsync((string _, string _, string path, StreamedFile _) =>
                new EntryDto { Path = path, Hash = "h", Type = EntryType.Generic, Size = 1 });
        var uploads = new ShareUploads(Store(), objects.Object, new FileSystem(), DdbManager(), TestPaths.Policy,
            NullLogger<ShareUploads>.Instance);
        var temp = new StreamedFile(Path.Combine(_root, "missing.tmp"), 1, new FileHashHint("h", 1, _time.Now));

        await uploads.UploadStreamed(token, "map.tif", temp);
        await uploads.UploadStreamed(token, "new.jpg", temp);

        _context.ChangeTracker.Clear();
        var entries = _context.Set<Entry>().AsNoTracking().OrderBy(e => e.Path).Select(e => new { e.Path, e.Replaced })
            .ToList();
        entries.Select(e => (e.Path, e.Replaced)).ShouldBe(new[] { ("map.tif", true), ("new.jpg", false) });
        _context.Batches.AsNoTracking().Single(b => b.Token == token).LastActivity.ShouldBe(_time.Now);
    }

    [Test]
    public async Task ACloseOfABatchThatEndedMeanwhile_IsRefused_AndKeepsTheFirstEnd()
    {
        var ds = AddDataset("raced");
        var token = AddBatch(ds, TimeSpan.FromHours(1), createdDataset: true);
        var store = Store();
        var batch = await store.GetRunningAsync(token);

        // The job (another context) rolled it back first
        await using (var other = _db.NewContext())
        {
            var stored = other.Batches.Single(b => b.Token == token);
            stored.Status = BatchStatus.Rolledback;
            stored.End = _time.Now;
            await other.SaveChangesAsync();
        }

        await Should.ThrowAsync<BadRequestException>(() => store.CloseAsync(batch, BatchStatus.Committed));
        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
    }

    [Test]
    public async Task TheManualRollback_KeepsAReplacedFile_AndADatasetThatHoldsOtherEntries()
    {
        var ds = AddDataset("manual");
        var token = AddBatch(ds, TimeSpan.FromHours(1), createdDataset: true, replaced: ["map.tif"],
            uploads: [("map.tif", TimeSpan.FromMinutes(50)), ("new.jpg", TimeSpan.FromMinutes(40))]);
        PutFile(ds, "colleague.jpg");
        var datasets = new Mock<IDatasetsManager>();
        var entries = new Mock<IEntryCommandService>();
        var store = Store();
        var rollback = new ShareBatchRollback(_context, Mock.Of<IDatasetAccessResolver>(), store, datasets.Object,
            entries.Object, DdbManager(), NullLogger<ShareBatchRollback>.Instance);

        await rollback.RollbackAsync(await store.GetRunningAsync(token), deleteDataset: true);

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        datasets.Verify(d => d.Delete(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        entries.Verify(e => e.DeleteAsync(Org, "manual", It.Is<string[]>(p => p.SequenceEqual(new[] { "new.jpg" })),
            It.IsAny<CancellationToken>()), Times.Once);
        entries.Verify(e => e.DeleteAsync(Org, "manual", It.Is<string[]>(p => p.Contains("map.tif")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheManualRollback_DeletesADatasetThatHoldsOnlyTheBatchFiles()
    {
        var ds = AddDataset("manual-only");
        var token = AddBatch(ds, TimeSpan.FromHours(1), createdDataset: true, uploads: ("a.jpg", TimeSpan.FromMinutes(50)));
        var datasets = new Mock<IDatasetsManager>();
        var store = Store();
        var rollback = new ShareBatchRollback(_context, Mock.Of<IDatasetAccessResolver>(), store, datasets.Object,
            Mock.Of<IEntryCommandService>(), DdbManager(), NullLogger<ShareBatchRollback>.Instance);

        await rollback.RollbackAsync(await store.GetRunningAsync(token), deleteDataset: true);

        datasets.Verify(d => d.Delete(Org, "manual-only", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task WhenTheFilesCannotBeRemoved_TheBatchStaysOpen_ForTheNextRun()
    {
        var ds = AddDataset("existing");
        var token = AddBatch(ds, TimeSpan.FromHours(48), createdDataset: false, uploads: ("a.jpg", TimeSpan.FromHours(47)));
        Fake(ds).Ddb.Setup(d => d.Remove(It.IsAny<string>())).Throws(new InvalidOperationException("index busy"));

        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Running);

        Fake(ds).Ddb.Setup(d => d.Remove(It.IsAny<string>())).Callback<string>(p => Fake(ds).Indexed.Remove(p));
        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        File.Exists(Local(ds, "a.jpg")).ShouldBeFalse();
    }

    [Test]
    public async Task ABatchThatCannotBeRolledBack_DoesNotStopTheOthers()
    {
        var first = AddDataset("one");
        var second = AddDataset("two");
        var failing = AddBatch(first, TimeSpan.FromDays(10), createdDataset: false, uploads: ("x.jpg", TimeSpan.FromDays(10)));
        var fine = AddBatch(second, TimeSpan.FromDays(9), createdDataset: true);
        Fake(first).Ddb.Setup(d => d.Remove("x.jpg")).Throws(new InvalidOperationException("index busy"));

        await Run();

        StatusOf(failing).ShouldBe(BatchStatus.Running);
        StatusOf(fine).ShouldBeNull();
        DatasetExists("two").ShouldBeFalse();
    }

    [Test]
    public async Task APreUpgradeBatchOnADatasetItDidNotCreate_IsClosed_AndKeepsEveryFile()
    {
        // Open at deploy time: map.tif was there before and the batch overwrote it, but Replaced is false for every
        // entry written before the migration, so which files were there before is unknown (FR-XC-27)
        var ds = AddDataset("open-at-deploy");
        PutFile(ds, "kept.jpg");
        var token = AddBatch(ds, TimeSpan.FromHours(48), createdDataset: false, preUpgrade: true,
            uploads: [("map.tif", TimeSpan.FromHours(47)), ("new.jpg", TimeSpan.FromHours(46))]);

        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        DatasetExists("open-at-deploy").ShouldBeTrue();
        File.Exists(Local(ds, "map.tif")).ShouldBeTrue("an overwritten file of a pre-upgrade batch is never deleted");
        File.Exists(Local(ds, "new.jpg")).ShouldBeTrue();
        Fake(ds).Indexed.Keys.Order().ShouldBe(["kept.jpg", "map.tif", "new.jpg"]);
        Fake(ds).Ddb.Verify(d => d.Remove(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task APreUpgradeBatchThatCreatedItsDataset_KeepsTheHoldsOnlyRule()
    {
        var alone = AddDataset("pre-upgrade-alone");
        var aloneToken = AddBatch(alone, TimeSpan.FromHours(48), createdDataset: true, preUpgrade: true,
            uploads: ("a.jpg", TimeSpan.FromHours(47)));
        var shared = AddDataset("pre-upgrade-shared");
        var sharedToken = AddBatch(shared, TimeSpan.FromHours(48), createdDataset: true, preUpgrade: true,
            uploads: ("b.jpg", TimeSpan.FromHours(47)));
        PutFile(shared, "colleague.jpg");

        await Run();

        StatusOf(aloneToken).ShouldBeNull();
        DatasetExists("pre-upgrade-alone").ShouldBeFalse();
        StatusOf(sharedToken).ShouldBe(BatchStatus.Rolledback);
        DatasetExists("pre-upgrade-shared").ShouldBeTrue();
        File.Exists(Local(shared, "b.jpg")).ShouldBeFalse();
        Fake(shared).Indexed.Keys.ShouldBe(["colleague.jpg"]);
    }

    [Test]
    public async Task APreUpgradeBatch_KeepsItsMarker_WhenUploadsGoOnAfterTheUpgrade()
    {
        var ds = AddDataset("pre-upgrade-busy");
        PutFile(ds, "map.tif");
        var token = AddBatch(ds, TimeSpan.FromHours(2), createdDataset: false, preUpgrade: true,
            uploads: ("old.tif", TimeSpan.FromHours(1)));
        var store = Store();

        await store.TouchAsync(token);
        await Uploads(ds, store).UploadStreamed(token, "map.tif", MissingTemp());

        _context.ChangeTracker.Clear();
        _context.Batches.AsNoTracking().Single(b => b.Token == token).LastActivity.ShouldBeNull();

        _time.Advance(TimeSpan.FromHours(25));
        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        Fake(ds).Indexed.Keys.Order().ShouldBe(["map.tif", "old.tif"]);
        File.Exists(Local(ds, "map.tif")).ShouldBeTrue();
    }

    [Test]
    public async Task ANewBatch_StartsWithItsStartAsLastActivity()
    {
        var ds = AddDataset("fresh");
        var tokens = new Mock<IBatchTokenGenerator>();
        tokens.Setup(t => t.GenerateToken()).Returns("fresh-token");
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync("alice");
        var store = new ShareBatchStore(_context, auth.Object, Mock.Of<IDatasetAccessResolver>(), tokens.Object, _time,
            NullLogger<ShareBatchStore>.Instance);

        var batch = await store.StartAsync(_context.Datasets.Single(d => d.Id == ds.Id), createdDataset: false);

        _context.ChangeTracker.Clear();
        _context.Batches.AsNoTracking().Single(b => b.Token == batch.Token).LastActivity
            .ShouldBe(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task TheManualRollbackOfAPreUpgradeBatch_DeletesNoFile()
    {
        var ds = AddDataset("manual-pre-upgrade");
        var token = AddBatch(ds, TimeSpan.FromHours(1), createdDataset: false, preUpgrade: true,
            uploads: [("map.tif", TimeSpan.FromMinutes(50)), ("new.jpg", TimeSpan.FromMinutes(40))]);
        var datasets = new Mock<IDatasetsManager>();
        var entries = new Mock<IEntryCommandService>();
        var store = Store();
        var rollback = new ShareBatchRollback(_context, Mock.Of<IDatasetAccessResolver>(), store, datasets.Object,
            entries.Object, DdbManager(), NullLogger<ShareBatchRollback>.Instance);

        await rollback.RollbackAsync(await store.GetRunningAsync(token));

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        entries.Verify(e => e.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
        datasets.Verify(d => d.Delete(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AShareUploadToANonCanonicalPath_RecordsThePathAsIndexed_AndTheRollbackKeepsTheReplacedFile()
    {
        // The dataset holds Maps/Ortho.TIF; the client sends a backslash and another letter case (D-040)
        var ds = AddDataset("non-canonical");
        PutFile(ds, "Maps", EntryType.Directory);
        PutFile(ds, "Maps/Ortho.TIF");
        var token = AddBatch(ds, TimeSpan.Zero, createdDataset: false);
        var committed = new List<string>();
        var uploads = Uploads(ds, Store(), committed);

        await uploads.UploadStreamed(token, "maps\\ortho.tif", MissingTemp());
        await uploads.UploadStreamed(token, "maps\\New.jpg", MissingTemp());
        await uploads.UploadStreamed(token, "./sub//a.jpg", MissingTemp());
        // A leading '/' is refused, as every upload refuses it, before anything is written or recorded
        (await Should.ThrowAsync<InvalidDatasetPathException>(() => uploads.UploadStreamed(token, "/x.jpg", MissingTemp())))
            .Rejection.ShouldBe(EntryPathRejection.Absolute);
        // The same file again, in yet another spelling, is the same entry of the batch
        await Should.ThrowAsync<BadRequestException>(() => uploads.UploadStreamed(token, "MAPS/ORTHO.TIF", MissingTemp()));

        committed.ShouldBe(["Maps/Ortho.TIF", "Maps/New.jpg", "sub/a.jpg"]);
        _context.ChangeTracker.Clear();
        _context.Set<Entry>().AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Path, e.Replaced }).ToList()
            .Select(e => (e.Path, e.Replaced))
            .ShouldBe(new[] { ("Maps/Ortho.TIF", true), ("Maps/New.jpg", false), ("sub/a.jpg", false) });

        _time.Advance(TimeSpan.FromHours(25));
        await Run();

        StatusOf(token).ShouldBe(BatchStatus.Rolledback);
        File.Exists(Local(ds, "Maps/Ortho.TIF")).ShouldBeTrue("FR-XC-27: the replaced file stays");
        File.Exists(Local(ds, "Maps/New.jpg")).ShouldBeFalse();
        File.Exists(Local(ds, "sub/a.jpg")).ShouldBeFalse();
        Fake(ds).Indexed.Keys.Order().ShouldBe(["Maps", "Maps/Ortho.TIF"]);
    }

    [Test]
    public async Task AChunkedShareUploadToANonCanonicalPath_RecordsThePathAsIndexed()
    {
        var ds = AddDataset("non-canonical-chunked");
        PutFile(ds, "Maps", EntryType.Directory);
        PutFile(ds, "Maps/Ortho.TIF");
        var token = AddBatch(ds, TimeSpan.Zero, createdDataset: false);
        var committed = new List<string>();
        var uploads = new ShareChunkedUploads(Store(), Mock.Of<IStorageQuota>(), new ChunkStore(), TestScratch.Provider,
            DdbManager(), Objects(ds, committed).Object, TestPaths.Policy, NullLogger<ShareChunkedUploads>.Instance);

        await using (var body = new MemoryStream("abcd"u8.ToArray()))
        {
            (await uploads.UploadChunk(token, new ChunkUploadDto
            {
                FileId = "ortho-file", Path = "maps\\ortho.tif", FileName = "ortho.tif", ChunkIndex = 0, TotalChunks = 1,
                TotalFileSize = 4
            }, body)).IsComplete.ShouldBeTrue();
        }

        var result = await uploads.FinalizeChunkedUpload(token, "ortho-file", "maps\\ortho.tif");

        result.Path.ShouldBe("Maps/Ortho.TIF");
        committed.ShouldBe(["Maps/Ortho.TIF"]);
        _context.ChangeTracker.Clear();
        var entry = _context.Set<Entry>().AsNoTracking().Single();
        (entry.Path, entry.Replaced).ShouldBe(("Maps/Ortho.TIF", true));
    }

    // The commit of a share upload: indexes the file at the path it is given (as the upload commit does)
    private Mock<IObjectsManager> Objects(Dataset ds, List<string>? committed = null)
    {
        var objects = new Mock<IObjectsManager>();
        objects.Setup(o => o.CommitStreamedAsync(Org, ds.Slug, It.IsAny<string>(), It.IsAny<StreamedFile>()))
            .ReturnsAsync((string _, string _, string path, StreamedFile _) =>
            {
                committed?.Add(path);
                PutFile(ds, path);
                return new EntryDto { Path = path, Hash = "h", Type = EntryType.Generic, Size = 1 };
            });
        return objects;
    }

    private ShareUploads Uploads(Dataset ds, ShareBatchStore store, List<string>? committed = null) =>
        new(store, Objects(ds, committed).Object, new FileSystem(), DdbManager(), TestPaths.Policy,
            NullLogger<ShareUploads>.Instance);

    private StreamedFile MissingTemp() =>
        new(Path.Combine(_root, "missing.tmp"), 1, new FileHashHint("h", 1, _time.Now));

    /// <summary>Commits a batch on the database right before the first statement that updates the batches, once armed.</summary>
    private sealed class CommitBeforeClose : DbCommandInterceptor
    {
        private string? _token;
        public bool Fired { get; private set; }

        public void Arm(string token) => _token = token;

        private void Before(DbCommand command)
        {
            if (_token is null || Fired || !command.CommandText.Contains("UPDATE \"Batches\"", StringComparison.Ordinal))
                return;

            Fired = true;
            using var commit = command.Connection!.CreateCommand();
            commit.Transaction = command.Transaction;
            commit.CommandText = "UPDATE \"Batches\" SET \"Status\" = $status WHERE \"Token\" = $token";
            var status = commit.CreateParameter();
            status.ParameterName = "$status";
            status.Value = (int)BatchStatus.Committed;
            commit.Parameters.Add(status);
            var token = commit.CreateParameter();
            token.ParameterName = "$token";
            token.Value = _token;
            commit.Parameters.Add(token);
            commit.ExecuteNonQuery();
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Before(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Before(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }
    }
}
