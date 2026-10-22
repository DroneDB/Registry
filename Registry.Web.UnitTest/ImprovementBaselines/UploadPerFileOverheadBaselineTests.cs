#nullable enable
using Registry.Web.Services.Jobs;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Test.Support;
using Registry.Web.Services.Builds;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// UPL-01 (ported from DroneDB-Roadmap/TBD/Improvements/UPL-01-upload-workflow-throughput/tests):
/// the per-file work of a burst of N single-file uploads into one dataset, each going through
/// what the upload endpoint does for one request (UploadManager: BeginAsync, StreamAsync,
/// CommitAsync). Before UPL-01 every file repeated 2 dataset lookups, 1 quota evaluation, 1 OGC
/// invalidation (2 Redis SCANs) and started 1 unbounded thumbnail task; those numbers are kept in
/// the roadmap baseline. These are the targets, now regression tests.
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class UploadPerFileOverheadBaselineTests : TestBase
{
    private const string OrgSlug = "org";
    private const string DsSlug = "ds";
    private const int Burst = 40;

    private string _root = null!;
    private Mock<IDatasetResolver> _utils = null!;
    private Mock<IStorageQuota> _quota = null!;
    private Mock<IAuthManager> _auth = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<ICacheManager> _cache = null!;
    private Mock<IThumbnailGenerator> _thumbs = null!;
    private Mock<IOgcCacheKeys> _ogcKeys = null!;
    private Dataset _ds = null!;

    private int _getDatasetCalls, _requestAccessCalls, _getCurrentUserCalls, _quotaEvaluations, _ogcInvalidations;
    private int _thumbInFlight, _thumbPeak, _thumbCalls;
    private readonly SemaphoreSlim _thumbGate = new(0);
    private readonly SemaphoreSlim _thumbStarted = new(0);

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "upl01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, IDDB.UploadsFolderName));

        var org = new Organization { Slug = OrgSlug, Name = "Org", CreationDate = DateTime.UtcNow, OwnerId = "u1" };
        _ds = new Dataset { Slug = DsSlug, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };

        _utils = new Mock<IDatasetResolver>();
        _quota = new Mock<IStorageQuota>();
        _utils.Setup(u => u.GetDatasetAsync(OrgSlug, DsSlug, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Interlocked.Increment(ref _getDatasetCalls); return _ds; });
        _quota.Setup(u => u.GetCurrentUserStorageLimit())
            .Returns(() =>
            {
                Interlocked.Increment(ref _quotaEvaluations);
                return Task.FromResult(new UserStorageInfo { Used = 0, Total = long.MaxValue });
            });

        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(() => { Interlocked.Increment(ref _requestAccessCalls); return Task.FromResult(true); });
        _auth.Setup(a => a.GetCurrentUser())
            .Returns(() => { Interlocked.Increment(ref _getCurrentUserCalls); return Task.FromResult(new User { Id = "u1", UserName = "u1" }); });

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(_root);
        _ddb.SetupGet(d => d.DerivedFolderPaths).Returns([Path.Combine(_root, ".ddb", "build")]);
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(_root, p));
        _ddb.SetupBuilds();
        _ddb.Setup(d => d.AddRaw(It.IsAny<string>()));
        _ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns<string>(p => new Entry
        {
            Path = p, Hash = "h-" + p, Size = 1,
            Type = p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? EntryType.Image : EntryType.Generic
        });
        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Get(OrgSlug, _ds.InternalRef)).Returns(_ddb.Object);

        // Thumbnail cache miss every time: run the factory so the generator is invoked like in production
        _cache = new Mock<ICacheManager>();
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns<string, string, object[]>(async (_, _, ps) =>
            {
                var factory = ps.OfType<Func<Task<byte[]>>>().FirstOrDefault();
                return factory != null ? await factory() : Array.Empty<byte>();
            });

        _thumbs = new Mock<IThumbnailGenerator>();
        _thumbs.Setup(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref _thumbCalls);
                var now = Interlocked.Increment(ref _thumbInFlight);
                int peak;
                do { peak = _thumbPeak; } while (now > peak && Interlocked.CompareExchange(ref _thumbPeak, now, peak) != peak);
                _thumbStarted.Release();
                await _thumbGate.WaitAsync(); // held until the test releases it: models a 100 ms native generation
                Interlocked.Decrement(ref _thumbInFlight);
            });

        _ogcKeys = new Mock<IOgcCacheKeys>();
        _ogcKeys.Setup(o => o.InvalidateAsync(OrgSlug, DsSlug))
            .Returns(() => { Interlocked.Increment(ref _ogcInvalidations); return Task.CompletedTask; });
    }

    [TearDown]
    public void TearDown()
    {
        _thumbGate.Release(Burst);
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private (UploadManager Uploads, DatasetChangeCoalescer Changes, ThumbnailPrecomputeQueue Thumbnails) CreatePipeline()
    {
        var ctx = TestDb.CreateContext<RegistryContext>();
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings { DatasetsPath = _root, DefaultThumbnailSize = 512 });
        var jobs = new Mock<IBackgroundJobsProcessor>();
        var buildPending = new BuildPendingService(ctx, new DatasetSweep(ctx), _ddbManager.Object, jobs.Object, _cache.Object, TestJobs.NewStorage(), Mock.Of<Microsoft.Extensions.Logging.ILogger<BuildPendingService>>());

        var changes = new DatasetChangeCoalescer(_ogcKeys.Object, _cache.Object, [], TimeSpan.FromMinutes(1),
            NullLogger<DatasetChangeCoalescer>.Instance);
        var thumbnails = new ThumbnailPrecomputeQueue(_cache.Object, _thumbs.Object, new UploadSettings(),
            NullLogger<ThumbnailPrecomputeQueue>.Instance);

        var uploads = new UploadManager(NullLogger<UploadManager>.Instance, _auth.Object,
            new DatasetAccessResolver(_utils.Object, _auth.Object, _ddbManager.Object, TestPaths.Policy), _ddbManager.Object, new Registry.Adapters.FileSystem(), TestSettings.Options<ThumbnailOptions>(settings), TestSettings.Options<ImportSettings>(settings), TestJobs.Scheduler(jobs.Object, buildPending),
            new UploadQuotaGuard(new UploadQuotaLedger(TestSettings.Options<UploadSettings>(settings)), _quota.Object), changes, TestPaths.Policy, TestScratch.Provider, thumbnails: thumbnails);
        return (uploads, changes, thumbnails);
    }

    /// <summary>Exactly what the upload endpoint does for a single-file request.</summary>
    private static async Task UploadOne(IUploadManager uploads, string path, int bytes)
    {
        using var ctx = await uploads.BeginAsync(OrgSlug, DsSlug, bytes);
        var file = await uploads.StreamAsync(ctx, new MemoryStream(new byte[bytes]));
        await uploads.CommitAsync(ctx, path, file);
    }

    private static Task UploadBurst(IUploadManager uploads, string ext) =>
        Task.WhenAll(Enumerable.Range(0, Burst).Select(i => UploadOne(uploads, $"f{i:000}.{ext}", 1024)));

    [Test]
    public async Task Target_BurstOfUploads_DoesNotRepeatPerRequestWorkPerFile()
    {
        var (uploads, changes, _) = CreatePipeline();
        await UploadBurst(uploads, "bin");
        await changes.FlushAsync();

        TestContext.Out.WriteLine($"burst={Burst}: GetDataset={_getDatasetCalls} RequestAccess={_requestAccessCalls} " +
                                  $"GetCurrentUser={_getCurrentUserCalls} QuotaEvaluations={_quotaEvaluations} OgcInvalidate={_ogcInvalidations}");

        _getDatasetCalls.ShouldBeLessThanOrEqualTo(Burst);          // one lookup per request, shared by stream + commit
        _requestAccessCalls.ShouldBeLessThanOrEqualTo(Burst);
        _getCurrentUserCalls.ShouldBeLessThanOrEqualTo(Burst);
        _ogcInvalidations.ShouldBe(1);                              // coalesced per dataset per burst, O(1) each
        _quotaEvaluations.ShouldBeLessThanOrEqualTo(1);             // one evaluation per burst, then running total
    }

    [Test]
    public async Task Target_ImageUploads_ThumbnailGenerationIsBounded()
    {
        var (uploads, _, thumbnails) = CreatePipeline();
        await UploadBurst(uploads, "jpg");

        // Every worker took a thumbnail and holds it: no further generation can start until the gate opens
        for (var i = 0; i < new UploadSettings().ThumbnailPrecomputeWorkers; i++)
            await _thumbStarted.WaitAsync();

        TestContext.Out.WriteLine($"thumbnail generations started={_thumbCalls}, peak concurrent={_thumbPeak}");
        _thumbPeak.ShouldBeLessThanOrEqualTo(2);  // bounded background worker, not one Task.Run per upload
        _thumbCalls.ShouldBeGreaterThan(0);

        _thumbGate.Release(Burst);
        await thumbnails.StopAsync(CancellationToken.None);
    }
}
