using Registry.Web.Services.Jobs;
using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Attributes;
using Registry.Web.Controllers;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// A push computes its delta once (Init) and Commit reuses it after checking the stamp
/// checksum; Commit asks the buildability of the adds with one batch call; an uploaded file is
/// streamed once to the dataset volume and moved into the push folder.
/// Real <see cref="PushManager"/> and file system (a temp dataset folder), counting native mocks.
/// </summary>
[TestFixture]
public class PushPipelineTests : TestBase
{
    private const int Adds = 100;

    private string _root = null!;
    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IDatasetResolver> _utils = null!;
    private Mock<IStorageQuota> _storageQuota = null!;
    private Mock<IObjectsManager> _objects = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private Mock<ICacheManager> _pendingCache = null!;
    private Dataset _ds = null!;
    private RegistryContext _ctx = null!;
    private Delta _delta = null!;
    private string _checksum = "c1";
    private bool _quotaExceeded;
    private int _quotaChecks;
    private UserStorageInfo? _quota;
    private UploadQuotaLedger _ledger = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "reg10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _checksum = "c1";
        _quotaExceeded = false;
        _quotaChecks = 0;
        _quota = null;
        _ledger = new UploadQuotaLedger(TimeSpan.FromMinutes(1));

        var org = new Organization { Slug = "org", Name = "Org", OwnerId = "u", CreationDate = DateTime.UtcNow };
        _ds = new Dataset { Id = 1, Slug = "ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };
        _ctx = TestDb.CreateContext<RegistryContext>();

        _delta = new Delta
        {
            Adds = [new AddAction("photos", ""), .. Enumerable.Range(0, Adds).Select(i => new AddAction($"photos/IMG_{i}.JPG", $"h{i}"))],
            Removes = [],
            MetaAdds = [],
            MetaRemoves = []
        };

        _wrapper = new Mock<IDdbWrapper>();
        _wrapper.Setup(w => w.GetStamp(_root)).Returns(() => new Stamp { Checksum = _checksum, Entries = [], Meta = [] });
        _wrapper.Setup(w => w.GetStampChecksum(_root)).Returns(() => _checksum);
        _wrapper.Setup(w => w.Delta(It.IsAny<Stamp>(), It.IsAny<Stamp>())).Returns(() => _delta);
        _wrapper.Setup(w => w.ComputeDeltaLocals(It.IsAny<Delta>(), _root, It.IsAny<string>()))
            .Returns(new Dictionary<string, bool>());
        _wrapper.Setup(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), _root, It.IsAny<MergeStrategy>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>())).Returns([]);

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(_root);
        _ddb.Setup(d => d.GetTmpFolder(It.IsAny<string>())).Returns((string p) =>
        {
            var folder = Path.Combine(_root, ".ddb", "tmp", p);
            Directory.CreateDirectory(folder);
            return folder;
        });
        // Every 10th photo is buildable
        _ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) =>
            paths.Select((p, i) => i % 10 == 0
                ? TestBuilds.Build(_root, p, EntryType.GeoRaster, "h-" + p, BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());
        _ddb.Setup(d => d.IsBuildPending()).Returns(false);

        _utils = new Mock<IDatasetResolver>();
        _storageQuota = new Mock<IStorageQuota>();
        _utils.Setup(u => u.GetDatasetAsync("org", "ds", It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_ds);
        // The storage evaluation behind the quota guard
        _storageQuota.Setup(u => u.GetCurrentUserStorageLimit()).ReturnsAsync(() =>
        {
            _quotaChecks++;
            return _quotaExceeded ? new UserStorageInfo { Used = 10, Total = 10 } : _quota;
        });

        _objects = new Mock<IObjectsManager>();

        _jobs = new Mock<IBackgroundJobsProcessor>();
        _pendingCache = new Mock<ICacheManager>();
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        Directory.Delete(_root, true);
    }

    private PushManager CreateManager()
    {
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get("org", _ds.InternalRef)).Returns(_ddb.Object);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = "u", UserName = "u" });
        auth.Setup(a => a.GetCurrentUserId()).ReturnsAsync("u");
        var cache = _pendingCache;
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings { DatasetsPath = _root });
        var fileSystem = new FileSystem();
        var buildPending = new BuildPendingService(new DatasetSweep(_ctx), ddbManager.Object, _jobs.Object, cache.Object, TestJobs.NewStorage(), Mock.Of<ILogger<BuildPendingService>>());
        return TestPush.Manager(_utils.Object, ddbManager.Object, Mock.Of<IDatasetsManager>(),
            auth.Object, new DatasetAccessResolver(_utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy), _wrapper.Object,
            TestJobs.Scheduler(_jobs.Object, buildPending, userId: "u"), TestSettings.Options<ImportSettings>(settings), fileSystem,
            new PushSessionStore(fileSystem), new UploadQuotaGuard(_ledger, _storageQuota.Object), TestPaths.Policy, TestScratch.Provider);
    }

    private static StampDto ClientStamp() => new() { Checksum = "client", Entries = [], Meta = [] };

    private string AddFolder(string token) => Path.Combine(_root, ".ddb", "tmp", "push-" + token, "add");

    // Stages every file add as if the client had uploaded it
    private void StageUploads(string token)
    {
        foreach (var add in _delta.Adds.Where(a => a.Hash.Length > 0))
        {
            var path = Path.Combine(AddFolder(token), add.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, add.Hash);
        }
    }

    private async Task<string> InitAndStage(PushManager mgr)
    {
        var res = await mgr.Init("org", "ds", "c1", ClientStamp());
        res.PullRequired.ShouldBeFalse();
        res.NeededFiles.ShouldNotBeNull().Length.ShouldBe(Adds);
        var token = res.Token.ShouldNotBeNull();
        StageUploads(token);
        return token;
    }

    /// <summary>
    /// The dataset is flagged as having pending builds before the first build job is queued,
    /// as uploads always did: a build that fails at once must find the flag, or the pending-build coordinator does not
    /// look at the dataset. Push queued its builds first and flagged after (red before the change).
    /// </summary>
    [Test]
    public async Task Commit_FlagsPendingBuildsBeforeQueueingThem()
    {
        var order = new List<string>();
        _pendingCache.Setup(c => c.SetAsync(MagicStrings.BuildPendingTrackerCacheSeed, It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<object[]>()))
            .Callback(() => order.Add("flag")).Returns(Task.CompletedTask);
        _jobs.Setup(j => j.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(),
                It.IsAny<IndexPayload>()))
            .Callback(() => order.Add("build")).Returns("job");
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);

        await mgr.Commit("org", "ds", token);

        order.ShouldContain("build");
        order[0].ShouldBe("flag");
    }

    /// <summary>
    /// A push retries the pending builds its files unblocked through the same path as an upload: one coordinator,
    /// none while it is live (red before: every push queued another pending-build job beside the live one).
    /// </summary>
    [Test]
    public async Task Commit_StartsThePendingRetryOnce_AndNoneWhileACoordinatorIsLive()
    {
        _ddb.Setup(d => d.IsBuildPending()).Returns(true);
        var methods = new List<string>();
        var statuses = new Dictionary<string, JobStatus>();
        _jobs.Setup(j => j.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<DdbJobs>>>(),
                It.IsAny<IndexPayload>()))
            .Returns<System.Linq.Expressions.Expression<Action<DdbJobs>>, IndexPayload>((job, _) =>
            {
                methods.Add(((System.Linq.Expressions.MethodCallExpression)job.Body).Method.Name);
                var id = "job-" + methods.Count;
                statuses[id] = JobStatus.Enqueued;
                return id;
            });
        _jobs.Setup(j => j.GetJobStatus(It.IsAny<string>()))
            .Returns<string>(id => statuses.GetValueOrDefault(id, JobStatus.Unknown));
        var mgr = CreateManager();

        await mgr.Commit("org", "ds", await InitAndStage(mgr));
        await mgr.Commit("org", "ds", await InitAndStage(mgr));

        methods.Count(m => m == nameof(DdbJobs.BuildPending)).ShouldBe(1);
        methods.Count(m => m == nameof(DdbJobs.Build)).ShouldBe(2 * Adds / 10, "the buildable pushed files");
    }

    [Test]
    public async Task Commit_ReusesInitDelta()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);

        await mgr.Commit("org", "ds", token);

        _wrapper.Verify(w => w.GetStamp(_root), Times.Once, "only Init builds the stamp");
        _wrapper.Verify(w => w.Delta(It.IsAny<Stamp>(), It.IsAny<Stamp>()), Times.Once, "only Init computes the delta");
        _wrapper.Verify(w => w.GetStampChecksum(_root), Times.Once);
        // Staged by hand, not uploaded: nothing is verified, libddb hashes every file
        _wrapper.Verify(w => w.ApplyDelta(It.Is<Delta>(d => d.Adds.Length == Adds + 1), AddFolder(token), _root,
            MergeStrategy.KeepTheirs, null, It.Is<IReadOnlyDictionary<string, string>>(v => v.Count == 0)), Times.Once);
        Directory.Exists(Path.GetDirectoryName(AddFolder(token))).ShouldBeFalse("the push folder is deleted after commit");
    }

    /// <summary>
    /// The request's token is the cancellation of the two native calls that read the pushed files, so
    /// a request that goes away stops them: <c>DDBComputeDeltaLocals</c> and <c>DDBApplyDelta</c> are cancellable and the
    /// apply is atomic to a cancel (red before: no token was current, the commit ran to completion with nobody waiting).
    /// </summary>
    [Test]
    public async Task Commit_GivesTheRequestsToken_ToTheNativeCallsThatReadThePushedFiles()
    {
        var seen = new Dictionary<string, CancellationToken>();
        _wrapper.Setup(w => w.ComputeDeltaLocals(It.IsAny<Delta>(), _root, It.IsAny<string>()))
            .Callback(() => seen["ComputeDeltaLocals"] = DdbCancellation.Current).Returns(new Dictionary<string, bool>());
        _wrapper.Setup(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), _root, It.IsAny<MergeStrategy>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback(() => seen["ApplyDelta"] = DdbCancellation.Current).Returns([]);
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);
        seen.Clear();
        using var request = new CancellationTokenSource();

        await mgr.Commit("org", "ds", token, request.Token);

        seen.Keys.ShouldBe(["ComputeDeltaLocals", "ApplyDelta"], ignoreOrder: true);
        seen.Values.ShouldAllBe(t => t == request.Token);
        DdbCancellation.Current.CanBeCanceled.ShouldBeFalse("the scope ends with the call");
    }

    [Test]
    public async Task Init_GivesTheRequestsToken_ToComputeDeltaLocals()
    {
        CancellationToken seen = default;
        _wrapper.Setup(w => w.ComputeDeltaLocals(It.IsAny<Delta>(), _root, It.IsAny<string>()))
            .Callback(() => seen = DdbCancellation.Current).Returns(new Dictionary<string, bool>());
        using var request = new CancellationTokenSource();

        await CreateManager().Init("org", "ds", "c1", ClientStamp(), request.Token);

        seen.ShouldBe(request.Token);
    }

    /// <summary>
    /// The apply stopped by a cancel before it changed anything (<c>DDBERR_CANCELED</c>): the commit is a cancel, not a
    /// failure, and leaves the push as it was: the session and the pushed files stay for the client's next commit, and
    /// nothing is queued to build.
    /// </summary>
    [Test]
    public async Task Commit_ApplyCanceledBeforeItChangesAnything_LeavesThePushForTheNextCommit()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);
        using var request = new CancellationTokenSource();
        _wrapper.Setup(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), _root, It.IsAny<MergeStrategy>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback(request.Cancel)
            .Throws(new DdbCanceledException("Canceled by the cancel token"));

        await Should.ThrowAsync<OperationCanceledException>(() => mgr.Commit("org", "ds", token, request.Token));

        Directory.Exists(Path.GetDirectoryName(AddFolder(token))).ShouldBeTrue("the push folder is kept");
        _jobs.Verify(j => j.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(),
            It.IsAny<IndexPayload>()), Times.Never);

        // The next commit finds the session and applies the delta
        _wrapper.Setup(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), _root, It.IsAny<MergeStrategy>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>())).Returns([]);
        await mgr.Commit("org", "ds", token);

        Directory.Exists(Path.GetDirectoryName(AddFolder(token))).ShouldBeFalse("the push folder is deleted after commit");
    }

    /// <summary>
    /// A cancel that comes after the apply passed its point of no return: libddb finishes it and returns normally, so the
    /// commit is a success whose request token is canceled: the push folder is deleted and the builds are queued
    /// (red before the apply was cancellable only by accident, and a half-applied delta could follow a cancel).
    /// </summary>
    [Test]
    public async Task Commit_ApplyThatFinishedAfterALateCancel_IsASuccess()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);
        using var request = new CancellationTokenSource();
        _wrapper.Setup(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), _root, It.IsAny<MergeStrategy>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback(request.Cancel).Returns([]);

        await mgr.Commit("org", "ds", token, request.Token);

        request.IsCancellationRequested.ShouldBeTrue();
        Directory.Exists(Path.GetDirectoryName(AddFolder(token))).ShouldBeFalse("the push folder is deleted after commit");
        _jobs.Verify(j => j.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(),
            It.IsAny<IndexPayload>()), Times.Exactly(Adds / 10));
    }

    [Test]
    public async Task Commit_AsksBuildabilityWithOneBatch()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);

        await mgr.Commit("org", "ds", token);

        _ddb.Verify(d => d.GetBuildArtifacts(It.Is<IReadOnlyList<string>>(p => p.Count == Adds && p[0] == "photos/IMG_0.JPG")), Times.Once);
        _jobs.Verify(j => j.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(),
            It.Is<IndexPayload>(m => m.Hash != null && m.Path!.StartsWith("photos/IMG_"))), Times.Exactly(Adds / 10));
    }

    [Test]
    public async Task Commit_DatasetChangedSinceInit_Throws()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);
        _checksum = "c2";

        var ex = await Should.ThrowAsync<ConflictException>(() => mgr.Commit("org", "ds", token));

        ex.Message.ShouldContain("changed by another user");
        _wrapper.Verify(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<MergeStrategy>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()), Times.Never);
    }

    [Test]
    public async Task Commit_MissingUpload_Throws()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);
        File.Delete(Path.Combine(AddFolder(token), "photos", "IMG_5.JPG"));

        var ex = await Should.ThrowAsync<BadRequestException>(() => mgr.Commit("org", "ds", token));
        ex.Message.ShouldContain("photos/IMG_5.JPG");
    }

    [Test]
    public async Task Commit_WithoutSession_Throws()
    {
        var ex = await Should.ThrowAsync<ConflictException>(() =>
            CreateManager().Commit("org", "ds", Guid.NewGuid().ToString()));
        ex.Message.ShouldContain("Push session not found");
    }

    [Test]
    public async Task Init_PullRequiredWhenClientIsBehind()
    {
        var res = await CreateManager().Init("org", "ds", "old-checksum", ClientStamp());

        res.PullRequired.ShouldBeTrue();
        _wrapper.Verify(w => w.Delta(It.IsAny<Stamp>(), It.IsAny<Stamp>()), Times.Never);
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The delta adds photos/IMG_{i}.JPG with the hash of "photo {i}"
    private void UseContentHashes()
    {
        _delta.Adds = [.. _delta.Adds.Select(a => a.Hash.Length == 0 ? a : new AddAction(a.Path, Sha256("photo " + a.Path)))];
    }

    private static async Task<PushUpload> Stream(PushManager mgr, string text) =>
        await mgr.StreamUploadAsync("org", "ds", new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8.GetByteCount(text));

    [Test]
    public async Task Upload_MovesStreamedFileIntoPushFolder()
    {
        UseContentHashes();
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token.ShouldNotBeNull();

        var upload = await Stream(mgr, "photo photos/IMG_0.JPG");
        upload.Bytes.ShouldBe(22);
        upload.Hash.Sha256.ShouldBe(Sha256("photo photos/IMG_0.JPG"));
        Path.GetDirectoryName(upload.TempPath).ShouldBe(Path.Combine(_root, IDDB.UploadsFolderName));
        await mgr.UploadAsync(upload, "photos/IMG_0.JPG", token);

        File.Exists(upload.TempPath).ShouldBeFalse();
        File.ReadAllText(Path.Combine(AddFolder(token), "photos", "IMG_0.JPG")).ShouldBe("photo photos/IMG_0.JPG");

        // A retried upload replaces the file
        upload = await Stream(mgr, "photo photos/IMG_0.JPG");
        await mgr.UploadAsync(upload, "photos/IMG_0.JPG", token);
        File.Exists(upload.TempPath).ShouldBeFalse();
    }

    [Test]
    public async Task Upload_RejectsBytesThatAreNotTheDeltas_BeforeStagingThem()
    {
        UseContentHashes();
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token.ShouldNotBeNull();
        var upload = await Stream(mgr, "a corrupted transfer");

        var ex = await Should.ThrowAsync<BadRequestException>(() => mgr.UploadAsync(upload, "photos/IMG_0.JPG", token));

        ex.Message.ShouldContain("does not match its hash");
        File.Exists(upload.TempPath).ShouldBeTrue("the controller deletes the refused temp file");
        File.Exists(Path.Combine(AddFolder(token), "photos", "IMG_0.JPG")).ShouldBeFalse();
    }

    [Test]
    public async Task Upload_RejectsAPathThePushDoesNotAdd()
    {
        UseContentHashes();
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token.ShouldNotBeNull();

        (await Should.ThrowAsync<BadRequestException>(async () =>
                await mgr.UploadAsync(await Stream(mgr, "x"), "photos/unknown.jpg", token)))
            .Message.ShouldContain("is not a file of this push");
        // A folder of the delta has no hash: not a file to upload
        await Should.ThrowAsync<BadRequestException>(async () =>
            await mgr.UploadAsync(await Stream(mgr, "x"), "photos", token));
    }

    [Test]
    public async Task Commit_HandsTheVerifiedUploadsToLibddb_AndOnlyThem()
    {
        UseContentHashes();
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token.ShouldNotBeNull();
        StageUploads(token); // every file staged as before verification existed (or linked)
        for (var i = 0; i < 3; i++)
            await mgr.UploadAsync(await Stream(mgr, $"photo photos/IMG_{i}.JPG"), $"photos/IMG_{i}.JPG", token);
        // A verified upload changed afterwards is not trusted any more
        File.AppendAllText(Path.Combine(AddFolder(token), "photos", "IMG_2.JPG"), "tampered");
        IReadOnlyDictionary<string, string>? verified = null;
        _wrapper.Setup(w => w.ApplyDelta(It.IsAny<Delta>(), It.IsAny<string>(), _root, It.IsAny<MergeStrategy>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<Delta, string, string, MergeStrategy, string, IReadOnlyDictionary<string, string>>(
                (_, _, _, _, _, v) => verified = v)
            .Returns([]);

        await mgr.Commit("org", "ds", token);

        verified.ShouldNotBeNull();
        verified.Keys.Order().ShouldBe(["photos/IMG_0.JPG", "photos/IMG_1.JPG"]);
        verified["photos/IMG_0.JPG"].ShouldBe(Sha256("photo photos/IMG_0.JPG"));
    }

    [Test]
    public async Task StreamUpload_ChecksQuotaBeforeWritingWhenSizeIsKnown()
    {
        _quotaExceeded = true;

        await Should.ThrowAsync<QuotaExceededException>(() =>
            CreateManager().StreamUploadAsync("org", "ds", new MemoryStream([1, 2, 3]), 3));

        Directory.Exists(Path.Combine(_root, IDDB.UploadsFolderName)).ShouldBeFalse("nothing was written");
    }

    [Test]
    public async Task StreamUpload_UnknownSize_ChecksQuotaAfterWritingAndDropsTheFile()
    {
        _quotaExceeded = true;

        await Should.ThrowAsync<QuotaExceededException>(() =>
            CreateManager().StreamUploadAsync("org", "ds", new MemoryStream([1, 2, 3]), null));

        _quotaChecks.ShouldBe(1);
        Directory.GetFiles(Path.Combine(_root, IDDB.UploadsFolderName)).ShouldBeEmpty();
    }

    // The files of a push add up against one storage evaluation (it used to check each
    // file against the stored use alone, with a user query and a query per owned dataset per file)
    [Test]
    public async Task StreamUpload_FilesOfAPushAddUpAgainstOneEvaluation()
    {
        _quota = new UserStorageInfo { Used = 0, Total = 10 };
        var mgr = CreateManager();

        for (var i = 0; i < 3; i++)
            (await mgr.StreamUploadAsync("org", "ds", new MemoryStream([1, 2, 3]), 3)).Bytes.ShouldBe(3);

        await Should.ThrowAsync<QuotaExceededException>(() =>
            mgr.StreamUploadAsync("org", "ds", new MemoryStream([1, 2, 3]), 3));
        _quotaChecks.ShouldBe(1, "one storage evaluation for the whole push");
    }

    [Test]
    public async Task SaveMeta_DoesNotCheckQuota()
    {
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token.ShouldNotBeNull();

        await mgr.SaveMeta("org", "ds", token, "[]");

        _quotaChecks.ShouldBe(0);
    }

    [Test]
    public void UploadAction_StreamsWithoutModelBinding()
    {
        var upload = typeof(PushController).GetMethod(nameof(PushController.Upload))!;

        upload.GetCustomAttribute<DisableFormValueModelBindingAttribute>().ShouldNotBeNull();
        upload.GetParameters().ShouldNotContain(p => p.ParameterType == typeof(IFormFile),
            "an IFormFile parameter makes MVC buffer the whole part to a temp file first");
    }

    [Test]
    public async Task InitAction_RejectsANullStamp()
    {
        var controller = new PushController(CreateManager(),
            Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Http.Features.FormOptions()), new FileSystem());

        await Should.ThrowAsync<BadRequestException>(() => controller.Init("org", "ds", "c1", "null", CancellationToken.None));
    }

    [Test]
    public void InitAction_AcceptsLargeStamps()
    {
        // A 50k-entry stamp is ~4.4 MiB, above the default 4 MiB form value limit
        var init = typeof(PushController).GetMethod(nameof(PushController.Init))!;

        init.GetCustomAttribute<RequestFormLimitsAttribute>()!.ValueLengthLimit.ShouldBe(int.MaxValue);
    }

    [Test]
    public async Task MultipartForm_FileBeforeFields_AndSingleFilePart()
    {
        var temps = new List<string>();
        Func<Stream, Task<(string, long)>> streamFile = async body =>
        {
            var temp = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            await using (var f = File.Create(temp))
                await body.CopyToAsync(f);
            temps.Add(temp);
            return (temp, new FileInfo(temp).Length);
        };

        // The CLI sends the file part first, then the fields
        var form = await MultipartRequestHelper.ReadSingleFileFormAsync(
            MultipartRequest(("file", "IMG.JPG", "jpeg"), ("path", null, "photos/IMG.JPG"), ("token", null, "t")),
            70, streamFile, temp => new Registry.Adapters.FileSystem().TryDelete(temp));
        form.GetField("path").ShouldBe("photos/IMG.JPG");
        form.GetField("TOKEN").ShouldBe("t");
        form.Bytes.ShouldBe(4);
        File.ReadAllText(form.TempFile.ShouldNotBeNull()).ShouldBe("jpeg");

        await Should.ThrowAsync<BadRequestException>(() => MultipartRequestHelper.ReadSingleFileFormAsync(
            MultipartRequest(("file", "a.jpg", "a"), ("file2", "b.jpg", "b")), 70, streamFile,
            temp => new Registry.Adapters.FileSystem().TryDelete(temp)));
        File.Exists(temps[1]).ShouldBeFalse("the first file of a rejected request is deleted");
    }

    private static HttpRequest MultipartRequest(params (string Name, string? FileName, string Content)[] parts)
    {
        const string boundary = "----reg10boundary";
        var sb = new StringBuilder();
        foreach (var (name, fileName, content) in parts)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append('"');
            if (fileName != null)
                sb.Append("; filename=\"").Append(fileName).Append('"');
            sb.Append("\r\n\r\n").Append(content).Append("\r\n");
        }
        sb.Append("--").Append(boundary).Append("--\r\n");

        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=" + boundary;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return context.Request;
    }
}
