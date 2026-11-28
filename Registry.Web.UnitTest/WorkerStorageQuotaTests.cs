using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="WorkerStorageQuota.EnsureToolOutputFitsAsync"/>, the one quota check of the tools that write one output
/// (merge, align, mask): an output that fits passes; one that does not fails the task with the 507, and when it replaced
/// a file it stays and is indexed again first, an index error then going to the task's log and never replacing the 507.
/// </summary>
[TestFixture]
public class WorkerStorageQuotaTests
{
    private const string Org = "org";
    private const string Output = "out/result.tif";

    private string _dir = null!;
    private string _file = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IHeavyToolExecutionContext> _ctx = null!;
    private List<HeavyToolProgress> _reports = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "workerquota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "result.tif");
        File.WriteAllBytes(_file, new byte[1000]);
        _ddb = new Mock<IDDB>();
        _ctx = new Mock<IHeavyToolExecutionContext>();
        _ctx.SetupGet(c => c.Ddb).Returns(_ddb.Object);
        _reports = [];
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    private void Remaining(long bytes)
    {
        var quota = new Mock<IStorageQuota>();
        quota.Setup(q => q.OwnerIdOfAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync("owner");
        quota.Setup(q => q.RemainingBytesAsync("owner", It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
        _ctx.SetupGet(c => c.Storage).Returns(quota.Object);
    }

    private Task Ensure(bool replacing) => WorkerStorageQuota.EnsureToolOutputFitsAsync(_ctx.Object, Org, Output,
        [_file], 0, replacing, new SyncProgress(_reports), CancellationToken.None);

    [Test]
    public async Task AnOutputThatFits_Passes_WithoutIndexingAgain()
    {
        Remaining(5000);

        await Ensure(replacing: true);

        _ddb.Verify(d => d.AddRaw(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ANewOutputThatDoesNotFit_FailsWith507_AndIsNotIndexed()
    {
        Remaining(10);

        await Should.ThrowAsync<QuotaExceededException>(() => Ensure(replacing: false));

        _ddb.Verify(d => d.AddRaw(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task AReplacedOutputThatDoesNotFit_IsIndexedAgain_ThenFailsWith507()
    {
        Remaining(10);

        await Should.ThrowAsync<QuotaExceededException>(() => Ensure(replacing: true));

        _ddb.Verify(d => d.AddRaw(Output), Times.Once);
        _reports.ShouldBeEmpty();
    }

    [Test]
    public async Task AnIndexErrorOfAReplacedOutput_GoesToTheLog_AndTheTaskStillFailsWith507()
    {
        Remaining(10);
        _ddb.Setup(d => d.AddRaw(Output)).Throws(new DdbBusyException("busy"));

        await Should.ThrowAsync<QuotaExceededException>(() => Ensure(replacing: true));

        _reports.ShouldHaveSingleItem().LogChunk.ShouldBe($"Could not index the replaced output {Output} again: busy");
    }

    // Reports on the calling thread (Progress<T> posts to the thread pool)
    private sealed class SyncProgress(List<HeavyToolProgress> reports) : IProgress<HeavyToolProgress>
    {
        public void Report(HeavyToolProgress value) => reports.Add(value);
    }
}
