#nullable enable
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
using Microsoft.Extensions.Options;
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
/// REG-10: a push computes its delta once (Init) and Commit reuses it after checking the stamp
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
    private Mock<IUtils> _utils = null!;
    private Mock<IObjectsManager> _objects = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
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
            It.IsAny<string>())).Returns([]);

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(_root);
        _ddb.Setup(d => d.GetTmpFolder(It.IsAny<string>())).Returns((string p) =>
        {
            var folder = Path.Combine(_root, ".ddb", "tmp", p);
            Directory.CreateDirectory(folder);
            return folder;
        });
        // Every 10th photo is buildable
        _ddb.Setup(d => d.GetBuildStatus(It.IsAny<string[]>())).Returns((string[] paths) =>
            paths.Select((p, i) => new EntryBuildStatus { Path = p, Exists = true, Buildable = i % 10 == 0 }).ToArray());
        _ddb.Setup(d => d.IsBuildPending()).Returns(false);

        _utils = new Mock<IUtils>();
        _utils.Setup(u => u.GetDatasetAsync("org", "ds", It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_ds);
        // The storage evaluation behind the quota guard
        _utils.Setup(u => u.GetCurrentUserStorageLimit()).ReturnsAsync(() =>
        {
            _quotaChecks++;
            return _quotaExceeded ? new UserStorageInfo { Used = 10, Total = 10 } : _quota;
        });

        _objects = new Mock<IObjectsManager>();

        _jobs = new Mock<IBackgroundJobsProcessor>();
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
        var cache = new Mock<ICacheManager>();
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings { DatasetsPath = _root });
        var fileSystem = new FileSystem();
        var buildPending = new BuildPendingService(_ctx, ddbManager.Object, _jobs.Object, cache.Object,
            Mock.Of<ILogger<BuildPendingService>>());
        return new PushManager(_utils.Object, ddbManager.Object, _objects.Object,
            CreateTestLogger<PushManager>(Serilog.Events.LogEventLevel.Warning), Mock.Of<IDatasetsManager>(),
            auth.Object, new DatasetAccessResolver(_utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy), _wrapper.Object, _jobs.Object, TestSettings.Options<ImportSettings>(settings), cache.Object, fileSystem, buildPending,
            new PushSessionStore(fileSystem), new UploadQuotaGuard(_ledger, _utils.Object), TestPaths.Policy, TestScratch.Provider);
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
        res.NeededFiles.Length.ShouldBe(Adds);
        StageUploads(res.Token);
        return res.Token;
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
        _wrapper.Verify(w => w.ApplyDelta(It.Is<Delta>(d => d.Adds.Length == Adds + 1), AddFolder(token), _root,
            MergeStrategy.KeepTheirs, null), Times.Once);
        Directory.Exists(Path.GetDirectoryName(AddFolder(token))).ShouldBeFalse("the push folder is deleted after commit");
    }

    [Test]
    public async Task Commit_AsksBuildabilityWithOneBatch()
    {
        var mgr = CreateManager();
        var token = await InitAndStage(mgr);

        await mgr.Commit("org", "ds", token);

        _ddb.Verify(d => d.IsBuildable(It.IsAny<string>()), Times.Never);
        _ddb.Verify(d => d.GetBuildStatus(It.Is<string[]>(p => p.Length == Adds && p[0] == "photos/IMG_0.JPG")), Times.Once);
        _jobs.Verify(j => j.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action>>(),
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
            It.IsAny<MergeStrategy>(), It.IsAny<string>()), Times.Never);
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

    [Test]
    public async Task Upload_MovesStreamedFileIntoPushFolder()
    {
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token;

        var upload = await mgr.StreamUploadAsync("org", "ds", new MemoryStream(Encoding.UTF8.GetBytes("jpeg")), 4);
        upload.Bytes.ShouldBe(4);
        Path.GetDirectoryName(upload.TempPath).ShouldBe(Path.Combine(_root, IDDB.UploadsFolderName));
        mgr.Upload(upload, "photos/IMG_0.JPG", token);

        File.Exists(upload.TempPath).ShouldBeFalse();
        File.ReadAllText(Path.Combine(AddFolder(token), "photos", "IMG_0.JPG")).ShouldBe("jpeg");

        // A retried upload replaces the file
        upload = await mgr.StreamUploadAsync("org", "ds", new MemoryStream(Encoding.UTF8.GetBytes("jpeg2")), 5);
        mgr.Upload(upload, "photos/IMG_0.JPG", token);
        File.ReadAllText(Path.Combine(AddFolder(token), "photos", "IMG_0.JPG")).ShouldBe("jpeg2");
    }

    [Test]
    public async Task Upload_RejectsUnknownTokensAndUnsafePaths()
    {
        var mgr = CreateManager();
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token;
        var upload = await mgr.StreamUploadAsync("org", "ds", new MemoryStream([1]), 1);

        Should.Throw<ConflictException>(() => mgr.Upload(upload, "a.jpg", Guid.NewGuid().ToString()));
        Should.Throw<BadRequestException>(() => mgr.Upload(upload, "a.jpg", "../../x"));
        Should.Throw<InvalidDatasetPathException>(() => mgr.Upload(upload, "../a.jpg", token));
        Should.Throw<InvalidDatasetPathException>(() => mgr.Upload(upload, "/etc/a.jpg", token));
        File.Exists(upload.TempPath).ShouldBeTrue("a rejected upload does not move the file");
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

    // REG-10: the files of a push add up against one storage evaluation (it used to check each
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
        var token = (await mgr.Init("org", "ds", "c1", ClientStamp())).Token;

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
        File.ReadAllText(form.TempFile).ShouldBe("jpeg");

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
