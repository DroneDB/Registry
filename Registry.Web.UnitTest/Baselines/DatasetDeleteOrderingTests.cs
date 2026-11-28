using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The folder of a deleted dataset is deleted only when no worker of the dataset is still running. A task stopped while it
/// runs may go on writing for a while (a native rescan cannot be interrupted, an import copies until it sees the cancel):
/// the cleanup stops its job but keeps the folder and the task's row while the row names a live job server, and the
/// worker, once it has stopped, queues the cleanup again (it finds its row under the tombstone slug), which then deletes
/// the folder. A mark left by a server that is no longer alive does not keep anything. The delete itself stops the
/// import or rescan of the dataset at once, so an import can no longer commit.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetDeleteOrderingTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class DatasetDeleteOrderingTests
{
    private const string Server = "worker-host:4242:0b5e";
    private static readonly Guid RefA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static RegistryContext Seeded(string? workerServer = Server, string? writeRecord = null) =>
        TestDb.CreateContext<RegistryContext>(context =>
        {
            var acme = Seed.Organization(context, "acme", ownerId: "admin-id");
            Seed.Dataset(context, acme, "alpha", RefA);
            context.JobIndices.AddRange(
                new JobIndex
                {
                    JobId = "import", OrgSlug = "acme", DsSlug = "alpha", ToolId = HeavyToolIds.ImportDataset,
                    CurrentState = "Processing", CreatedAtUtc = DateTime.UtcNow, WorkerServerId = workerServer,
                    WriteRecordJson = writeRecord
                },
                new JobIndex
                {
                    JobId = "build", OrgSlug = "acme", DsSlug = "alpha", ToolId = HeavyToolIds.Build,
                    CurrentState = "Enqueued", CreatedAtUtc = DateTime.UtcNow
                },
                new JobIndex
                {
                    JobId = "old", OrgSlug = "acme", DsSlug = "alpha", CurrentState = "Succeeded",
                    CreatedAtUtc = DateTime.UtcNow
                });
            context.SaveChanges();
        });

    private static string[] Rows(RegistryContext context) =>
        [.. context.JobIndices.AsNoTracking().Select(j => j.JobId).OrderBy(j => j)];

    private static TaskWorkerMarker Marker(RegistryContext context, CleanupJobRecorder queue) =>
        new(context, queue.Jobs.Object, NullLogger<TaskWorkerMarker>.Instance);

    // The dataset is deleted, then its cleanup runs with these live job servers
    private static async Task<(CleanupJobRecorder Queue, Mock<IDdbManager> Ddb, FakeLiveJobServers Servers)>
        DeleteAndCleanUpAsync(RegistryContext context, params string[] liveServers)
    {
        var queue = new CleanupJobRecorder();
        queue.Jobs.Setup(j => j.Delete("import", "Processing")).Returns(true);
        queue.Jobs.Setup(j => j.Delete("build", "Enqueued")).Returns(true);
        var servers = new FakeLiveJobServers();
        servers.Live.UnionWith(liveServers);
        var ddb = new Mock<IDdbManager>();

        await TestDatasets.AdminManager(context, queue.Jobs.Object).Delete("acme", "alpha");
        await queue.RunAsync(TestJobs.Cleanup(context, queue.Jobs.Object, ddb.Object, servers));
        return (queue, ddb, servers);
    }

    /// <summary>Red before: the cleanup deleted the folder right after it deleted the jobs, under the running worker.</summary>
    [Test]
    public async Task AWorkerStillRunning_KeepsTheFolderAndItsRow_WhileItsJobIsStopped()
    {
        await using var context = Seeded();

        var (queue, ddb, _) = await DeleteAndCleanUpAsync(context, Server);

        ddb.Verify(m => m.Delete(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never, "a worker may still write there");
        Rows(context).ShouldBe(["import"], "the row of the running worker stays, the others go");
        queue.Jobs.Verify(j => j.Delete("import", "Processing"), Times.AtLeastOnce);
        queue.Jobs.Verify(j => j.Delete("build", "Enqueued"), Times.Once, "the cleanup stops the other jobs");
    }

    [Test]
    public async Task TheWorkersEnd_QueuesTheCleanupAgain_WhichDeletesTheFolder()
    {
        await using var context = Seeded();
        var (_, ddb, servers) = await DeleteAndCleanUpAsync(context, Server);

        // The worker stops (the end of HeavyTaskJobWrapper.Run): its mark goes, and its row is under the tombstone
        var end = new CleanupJobRecorder();
        await Marker(context, end).ClearAsync("import");

        end.Cleanup.ShouldNotBeNull("the worker queues the cleanup of its deleted dataset");
        await end.RunAsync(TestJobs.Cleanup(context, end.Jobs.Object, ddb.Object, servers));
        ddb.Verify(m => m.Delete("acme", RefA), Times.Once);
        Rows(context).ShouldBeEmpty();
    }

    /// <summary>A server that crashed is no longer in the live list: the mark it left does not keep the folder.</summary>
    [Test]
    public async Task TheMarkOfAServerThatIsGone_DoesNotKeepTheFolder()
    {
        await using var context = Seeded();

        var (_, ddb, _) = await DeleteAndCleanUpAsync(context, "another-host:1:aa");

        ddb.Verify(m => m.Delete("acme", RefA), Times.Once);
        Rows(context).ShouldBeEmpty();
    }

    [Test]
    public async Task NoWorkerRunning_DeletesTheFolderAtOnce()
    {
        await using var context = Seeded(workerServer: null);

        var (_, ddb, _) = await DeleteAndCleanUpAsync(context, Server);

        ddb.Verify(m => m.Delete("acme", RefA), Times.Once);
        Rows(context).ShouldBeEmpty();
    }

    /// <summary>A worker that ends on a dataset that still exists queues nothing.</summary>
    [Test]
    public async Task AWorkersEnd_WithoutADelete_QueuesNoCleanup()
    {
        await using var context = Seeded();
        var end = new CleanupJobRecorder();

        await Marker(context, end).ClearAsync("import");

        end.Cleanup.ShouldBeNull();
        context.JobIndices.AsNoTracking().Single(j => j.JobId == "import").WorkerServerId.ShouldBeNull();
    }

    /// <summary>
    /// The delete stops the import at once, before its cleanup runs: its write record is closed to a commit first, then
    /// its job is deleted. The other jobs are left to the cleanup.
    /// </summary>
    [Test]
    public async Task TheDelete_StopsTheImportAtOnce_AndItCanNoLongerCommit()
    {
        await using var context = Seeded(writeRecord: new TaskWriteRecord().ToJson());
        var queue = new CleanupJobRecorder();
        queue.Jobs.Setup(j => j.Delete("import", "Processing")).Returns(true);

        await TestDatasets.AdminManager(context, queue.Jobs.Object).Delete("acme", "alpha");

        queue.Jobs.Verify(j => j.Delete("import", "Processing"), Times.Once);
        queue.Jobs.Verify(j => j.Delete("build", It.IsAny<string>()), Times.Never);
        var record = TaskWriteRecord.Parse(context.JobIndices.AsNoTracking().Single(j => j.JobId == "import").WriteRecordJson);
        record!.State.ShouldBe(TaskWriteState.CancelRequested);
        queue.Cleanup.ShouldNotBeNull();
    }
}
