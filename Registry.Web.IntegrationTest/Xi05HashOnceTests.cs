#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// XI-05 on the real libddb: each pushed or copied byte is read once. The bytes the process reads
/// (<c>/proc/self/io</c> rchar) are measured around a push commit, whose uploads were hashed while
/// they streamed and checked against the delta (libddb's <c>DDBApplyDeltaEx</c> trusts them), and
/// around the indexing of a transferred file (indexed with the source hash, <c>DDBAddWithHints</c>).
/// Before XI-05 libddb read every pushed file again in <c>applyDelta</c>, and hashed every copy.
/// </summary>
[TestFixture]
[NonParallelizable]
[Platform("Linux")]
[Category(TestCategories.Native)]
public class Xi05HashOnceTests
{
    private const int Files = 4;
    private const int FileBytes = 16 * 1024 * 1024;
    private const int TotalBytes = Files * FileBytes;

    private string _root = null!;
    private readonly NativeDdbWrapper _wrapper = new();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "xi05-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        _wrapper.ReleaseConnections(_root);
        Directory.Delete(_root, true);
    }

    private static long BytesRead() =>
        File.ReadLines("/proc/self/io").Where(l => l.StartsWith("rchar:", StringComparison.Ordinal))
            .Select(l => long.Parse(l["rchar:".Length..].Trim(), CultureInfo.InvariantCulture)).Single();

    private DDB NewDataset(string folder)
    {
        Directory.CreateDirectory(folder);
        var ddb = new DDB(folder, _wrapper);
        ddb.Init();
        return ddb;
    }

    private static void WriteRandom(string path, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[FileBytes];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    [Test]
    public async Task PushCommit_DoesNotReadTheUploadedFilesAgain()
    {
        // The client's dataset: what the push sends
        var client = NewDataset(Path.Combine(_root, "client"));
        for (var i = 0; i < Files; i++)
        {
            WriteRandom(Path.Combine(client.DatasetFolderPath, $"data/file{i}.bin"), i);
            client.AddRaw($"data/file{i}.bin");
        }
        var clientStamp = _wrapper.GetStamp(client.DatasetFolderPath);

        // The server's dataset
        var internalRef = Guid.NewGuid();
        var target = NewDataset(Path.Combine(_root, "datasets", "org", internalRef.ToString()));
        await using var db = TestDb.CreateContext<RegistryContext>();
        var org = new Organization { Slug = "org", Name = "org", OwnerId = "u", CreationDate = DateTime.UtcNow };
        var ds = new Dataset { Slug = "ds", InternalRef = internalRef, CreationDate = DateTime.UtcNow, Organization = org };
        db.Organizations.Add(org);
        db.Datasets.Add(ds);
        await db.SaveChangesAsync();

        var manager = CreatePushManager(db, ds, target);
        var init = await manager.Init("org", "ds", _wrapper.GetStampChecksum(target.DatasetFolderPath),
            new StampDto { Checksum = clientStamp.Checksum, Entries = clientStamp.Entries, Meta = clientStamp.Meta });
        init.NeededFiles.Length.ShouldBe(Files);
        foreach (var path in init.NeededFiles)
        {
            await using var body = File.OpenRead(Path.Combine(client.DatasetFolderPath, path));
            var upload = await manager.StreamUploadAsync("org", "ds", body, FileBytes);
            await manager.UploadAsync(upload, path, init.Token);
        }

        var before = BytesRead();
        await manager.Commit("org", "ds", init.Token);
        var read = BytesRead() - before;

        TestContext.Out.WriteLine($"Pushed {TotalBytes:N0} bytes in {Files} files; read by the commit: {read:N0} bytes");
        target.EntryExists("data/file0.bin").ShouldBeTrue();
        target.GetEntry("data/file3.bin")!.Hash.ShouldBe(client.GetEntry("data/file3.bin")!.Hash);
        read.ShouldBeLessThan(TotalBytes / 8, "the commit does not hash the uploads again");
    }

    private PushManager CreatePushManager(RegistryContext db, Dataset ds, IDDB target)
    {
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get("org", ds.InternalRef)).Returns(target);
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync("org", "ds", It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ds);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = "u", UserName = "u" });
        auth.Setup(a => a.GetCurrentUserId()).ReturnsAsync("u");
        var jobs = new Mock<IBackgroundJobsProcessor>();
        var cache = new Mock<ICacheManager>();
        var fileSystem = new FileSystem();
        return new PushManager(utils.Object, ddbManager.Object, Mock.Of<IObjectsManager>(),
            NullLogger<PushManager>.Instance, Mock.Of<IDatasetsManager>(), auth.Object,
            new DatasetAccessResolver(utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy), _wrapper,
            jobs.Object, Microsoft.Extensions.Options.Options.Create(new ImportSettings()), cache.Object, fileSystem,
            TestJobs.BuildPendingService(db, ddbManager.Object, jobs.Object, cache.Object), new PushSessionStore(fileSystem),
            new UploadQuotaGuard(new UploadQuotaLedger(TimeSpan.FromMinutes(1)), Mock.Of<IStorageQuota>()), TestPaths.Policy,
            TestScratch.Provider);
    }

    [Test]
    public void TransferredFile_IsIndexedWithTheSourceHash_NotHashedAgain()
    {
        var source = NewDataset(Path.Combine(_root, "source"));
        WriteRandom(Path.Combine(source.DatasetFolderPath, "big.bin"), 7);
        source.AddRaw("big.bin");
        var sourceEntry = source.GetEntry("big.bin")!;
        var dest = NewDataset(Path.Combine(_root, "dest"));
        var fs = new FileSystem();

        // Before XI-05: the copy indexed with AddRaw, libddb hashes it
        File.Copy(source.GetLocalPath("big.bin"), dest.GetLocalPath("before.bin"));
        var start = BytesRead();
        dest.AddRaw("before.bin");
        var readBefore = BytesRead() - start;

        // After: indexed with the source entry's hash
        File.Copy(source.GetLocalPath("big.bin"), dest.GetLocalPath("after.bin"));
        start = BytesRead();
        CopiedEntries.Index(source, new EntrySummary
        {
            Path = sourceEntry.Path, Hash = sourceEntry.Hash, Type = sourceEntry.Type, Size = sourceEntry.Size,
            ModifiedTime = sourceEntry.ModifiedTime
        }, dest, "after.bin", fs);
        var readAfter = BytesRead() - start;

        TestContext.Out.WriteLine($"Copied {FileBytes:N0} bytes; read to index the copy: before {readBefore:N0}, after {readAfter:N0}");
        dest.GetEntry("after.bin")!.Hash.ShouldBe(sourceEntry.Hash);
        dest.GetEntry("before.bin")!.Hash.ShouldBe(sourceEntry.Hash);
        readBefore.ShouldBeGreaterThan(FileBytes - 1, "AddRaw hashes the copy");
        readAfter.ShouldBeLessThan(FileBytes / 8, "the hint is used: the copy is not read");
    }

    [Test]
    public void TransferredFolder_HintsEveryIndexedFile_AndStillAddsTheRest()
    {
        var source = NewDataset(Path.Combine(_root, "source"));
        for (var i = 0; i < 2; i++)
        {
            WriteRandom(Path.Combine(source.DatasetFolderPath, $"dir/sub/f{i}.bin"), 20 + i);
            source.AddRaw($"dir/sub/f{i}.bin");
        }
        File.WriteAllText(Path.Combine(source.DatasetFolderPath, "dir", "not-indexed.txt"), "x");
        var dest = NewDataset(Path.Combine(_root, "dest"));
        new FileSystem().FolderCopy(source.GetLocalPath("dir"), dest.GetLocalPath("moved"));
        var fs = new FileSystem();

        var hints = CopiedEntries.Hints(source, source.GetEntry("dir")!, dest, "moved", fs);
        hints.Select(h => h.Path).Order().ShouldBe(["moved/sub/f0.bin", "moved/sub/f1.bin"]);

        var start = BytesRead();
        CopiedEntries.Index(source, source.GetEntry("dir")!, dest, "moved", fs);
        var read = BytesRead() - start;

        TestContext.Out.WriteLine($"Folder of {2 * FileBytes:N0} indexed bytes; read to index the copy: {read:N0}");
        dest.EntryExists("moved/sub").ShouldBeTrue();
        dest.EntryExists("moved/not-indexed.txt").ShouldBeTrue("a file the source did not index is still added");
        dest.GetEntry("moved/sub/f1.bin")!.Hash.ShouldBe(source.GetEntry("dir/sub/f1.bin")!.Hash);
        read.ShouldBeLessThan(FileBytes / 8);
    }
}
