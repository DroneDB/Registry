using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The chunk sessions of share batches belong to the user that sends them, as the dataset chunk uploads do: a user with
/// a batch of their own on the same dataset cannot add chunks to the session of another user with the same file id,
/// take it with a finalize, or reach a dataset chunk upload by the name it has in the chunks folder.
/// </summary>
[TestFixture]
public class ShareChunkedUploadsTests
{
    private const string Org = "org";

    private TestDb<RegistryContext> _db = null!;
    private RegistryContext _context = null!;
    private string _root = null!;
    private string _user = "alice";
    private ChunkStore _chunks = null!;
    private Dataset _dataset = null!;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.Create<RegistryContext>();
        _context = _db.NewContext();
        var org = new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow, Datasets = [] };
        _dataset = new Dataset { Slug = "ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };
        _context.Organizations.Add(org);
        _context.Datasets.Add(_dataset);
        foreach (var user in new[] { "alice", "bob" })
            _context.Batches.Add(new Batch
            {
                Token = "batch-" + user, Dataset = _dataset, UserName = user, Start = DateTime.UtcNow,
                Status = BatchStatus.Running, LastActivity = DateTime.UtcNow, Entries = []
            });
        _context.SaveChanges();

        _root = Path.Combine(Path.GetTempPath(), "share-chunks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _chunks = new ChunkStore();
        _user = "alice";
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string IdOf(string user) => user + "-id";

    private string ChunksRoot => Path.Combine(_root, IDDB.UploadsFolderName, Registry.Web.Utilities.DatasetUploads.ChunksFolderName);

    private ShareChunkedUploads Uploads()
    {
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync(() => _user);
        auth.Setup(a => a.GetCurrentUserId()).ReturnsAsync(() => IdOf(_user));
        auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        var store = new ShareBatchStore(_context, auth.Object, Mock.Of<IDatasetAccessResolver>(),
            Mock.Of<IBatchTokenGenerator>(), TimeProvider.System, Mock.Of<Registry.Web.Services.Datasets.IDatasetActivity>(),
            NullLogger<ShareBatchStore>.Instance);

        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_root);
        ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns([]);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);

        return new ShareChunkedUploads(store, Mock.Of<IStorageQuota>(),
            new BatchChunkSessions(_chunks, ddbManager.Object, auth.Object),
            Mock.Of<IObjectsManager>(), TestPaths.Policy, NullLogger<ShareChunkedUploads>.Instance);
    }

    private Task<ChunkUploadResultDto> SendChunk(string fileId, int index, string path = "orthophoto.tif")
    {
        var body = new MemoryStream("abcd"u8.ToArray());
        return Uploads().UploadChunk("batch-" + _user, new ChunkUploadDto
        {
            FileId = fileId, Path = path, FileName = path, ChunkIndex = index, TotalChunks = 2, TotalFileSize = 8,
            ChunkSize = 4
        }, body);
    }

    [Test]
    public async Task AChunkOfAnotherUserWithTheSameFileId_GoesToTheirOwnSession()
    {
        await SendChunk("shared-file-01", 0);

        _user = "bob";
        var bobs = await SendChunk("shared-file-01", 1);

        bobs.IsComplete.ShouldBeFalse("bob's chunk must not complete alice's upload");
        var alice = _chunks.GetStatus(ChunksRoot, ChunkedUploadManager.SessionKey(IdOf("alice"), "shared-file-01"))!;
        alice.Received.ShouldBe([0]);
        alice.Manifest.UserId.ShouldBe(IdOf("alice"));
        _chunks.GetStatus(ChunksRoot, ChunkedUploadManager.SessionKey(IdOf("bob"), "shared-file-01"))!.Received
            .ShouldBe([1]);
        _chunks.GetStatus(ChunksRoot, "shared-file-01").ShouldBeNull("no session is named by the raw file id");
    }

    [Test]
    public async Task AFinalizeOfAnotherUser_DoesNotTakeTheSession()
    {
        await SendChunk("shared-file-02", 0);
        (await SendChunk("shared-file-02", 1)).IsComplete.ShouldBeTrue();

        _user = "bob";
        await Should.ThrowAsync<NotFoundException>(() =>
            Uploads().FinalizeChunkedUpload("batch-bob", "shared-file-02", "orthophoto.tif"));

        _chunks.GetStatus(ChunksRoot, ChunkedUploadManager.SessionKey(IdOf("alice"), "shared-file-02"))!.Complete
            .ShouldBeTrue("alice's upload is still there to finalize");
    }

    [Test]
    public async Task ADatasetChunkUploadOfAnotherUser_CannotBeReachedByItsName()
    {
        // A dataset chunk upload of alice, complete, named in the chunks folder by its session key
        var key = ChunkedUploadManager.SessionKey(IdOf("alice"), "client-file-01");
        await _chunks.OpenAsync(ChunksRoot, key, new ChunkManifest("data.bin", 4, 1, 4, IdOf("alice"), FileId: "client-file-01"));
        await _chunks.WriteAsync(ChunksRoot, key, 0, new MemoryStream("abcd"u8.ToArray()));

        _user = "bob";
        await Should.ThrowAsync<NotFoundException>(() =>
            Uploads().FinalizeChunkedUpload("batch-bob", key, "stolen.bin"));
        (await SendChunk(key, 0)).IsComplete.ShouldBeFalse();

        var alice = _chunks.GetStatus(ChunksRoot, key)!;
        alice.Complete.ShouldBeTrue();
        alice.Manifest.Path.ShouldBe("data.bin");
        _chunks.List(ChunksRoot).Count(s => s.Manifest.UserId == IdOf("bob")).ShouldBe(1);
    }
}
