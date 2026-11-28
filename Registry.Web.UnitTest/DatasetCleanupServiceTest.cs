using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// The cleanup of a deleted dataset: it stops the jobs queued or running in any blocking state (the one stop of a task,
/// <c>TaskCanceller</c>), removes the rows and deletes the folder; a job that cannot be stopped does not stop the others.
/// What it does while a worker still runs is in <c>DatasetDeleteOrderingTests</c>.
/// </summary>
[TestFixture]
public class DatasetCleanupServiceTest : TestBase
{
    private const string OrgSlug = "test-org";
    private const string DsSlug = "test-ds";

    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;

    [SetUp]
    public void Setup()
    {
        _ddbManagerMock = new Mock<IDdbManager>();
        _jobs = new Mock<IBackgroundJobsProcessor>();
    }

    private DatasetCleanupService Service(RegistryContext context) =>
        TestJobs.Cleanup(context, _jobs.Object, _ddbManagerMock.Object);

    private static JobIndex Job(string id, string state) => new()
    {
        JobId = id, OrgSlug = OrgSlug, DsSlug = DsSlug, CurrentState = state, CreatedAtUtc = DateTime.UtcNow
    };

    private static RegistryContext ContextWith(params JobIndex[] jobs) => TestDb.CreateContext<RegistryContext>(context =>
    {
        context.JobIndices.AddRange(jobs);
        context.SaveChanges();
    });

    [Test]
    public async Task CleanupDeletedDatasetAsync_NoActiveJobs_DeletesFilesystem()
    {
        var internalRef = Guid.NewGuid();
        await using var context = ContextWith();

        await Service(context).CleanupDeletedDatasetAsync(OrgSlug, DsSlug, internalRef, null);

        _ddbManagerMock.Verify(x => x.Delete(OrgSlug, internalRef), Times.Once);
        _jobs.Verify(x => x.Delete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>Every blocking state is stopped, Indexing too (the cleanup used to keep a list of its own without it).</summary>
    [TestCase("Indexing")]
    [TestCase("Awaiting")]
    [TestCase("Scheduled")]
    [TestCase("Enqueued")]
    [TestCase("Processing")]
    public async Task CleanupDeletedDatasetAsync_StopsAJobIn(string state)
    {
        var internalRef = Guid.NewGuid();
        await using var context = ContextWith(Job("job-1", state), Job("job-2", "Succeeded"));
        _jobs.Setup(x => x.Delete("job-1", state)).Returns(true);

        await Service(context).CleanupDeletedDatasetAsync(OrgSlug, DsSlug, internalRef, null);

        _jobs.Verify(x => x.Delete("job-1", state), Times.Once);
        _jobs.Verify(x => x.Delete("job-2", It.IsAny<string>()), Times.Never, "a job that ended is not stopped");
        _ddbManagerMock.Verify(x => x.Delete(OrgSlug, internalRef), Times.Once);
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_RemovesJobIndexEntries()
    {
        await using var context = ContextWith(Job("job-1", "Succeeded"), Job("job-2", "Failed"), Job("job-3", "Processing"),
            new JobIndex { JobId = "other", OrgSlug = OrgSlug, DsSlug = "other-ds", CurrentState = "Processing" });

        await Service(context).CleanupDeletedDatasetAsync(OrgSlug, DsSlug, Guid.NewGuid(), null);

        (await context.JobIndices.Select(j => j.JobId).ToArrayAsync()).ShouldBe(["other"]);
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_FilesystemDeleteFails_DoesNotThrow()
    {
        var internalRef = Guid.NewGuid();
        await using var context = ContextWith();
        // Simulate filesystem delete failure (file locked)
        _ddbManagerMock.Setup(x => x.Delete(OrgSlug, internalRef))
            .Throws(new System.IO.IOException("The process cannot access the file because it is being used"));

        await Should.NotThrowAsync(() => Service(context).CleanupDeletedDatasetAsync(OrgSlug, DsSlug, internalRef, null));
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_JobCancellationFails_ContinuesWithOtherJobs()
    {
        var internalRef = Guid.NewGuid();
        await using var context = ContextWith(Job("job-1", "Processing"), Job("job-2", "Enqueued"));
        // First job cancellation fails, second succeeds
        _jobs.Setup(x => x.Delete("job-1", It.IsAny<string>())).Throws(new InvalidOperationException("Job not found"));
        _jobs.Setup(x => x.Delete("job-2", "Enqueued")).Returns(true);

        await Service(context).CleanupDeletedDatasetAsync(OrgSlug, DsSlug, internalRef, null);

        _jobs.Verify(x => x.Delete("job-2", "Enqueued"), Times.Once);
        _ddbManagerMock.Verify(x => x.Delete(OrgSlug, internalRef), Times.Once);
    }
}
