#nullable enable
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
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Follow-ups: what the jobs of a dataset do when its slug or organization changes or when it is deleted.
/// <list type="bullet">
/// <item>A deleted dataset's job rows leave with it, to its tombstone slug, and the cleanup job acts on those: a dataset
/// that takes the slug before the cleanup runs (a rename or a move into it) keeps its own history and jobs (the cleanup
/// used to delete and cancel by slug).</item>
/// <item>A dataset with jobs queued or running is not renamed or moved: a job names its dataset by the slugs it was queued
/// with (Hangfire arguments, job parameters, the slugs of a task request), so one that outlived the change would not find
/// it. The rule is the one of the slug repair (<see cref="DatasetJobsRule"/>).</item>
/// </list>
/// The same scenarios run on MariaDB in <c>JobsAcrossSlugChangesMariaDbTests</c> (Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~JobsAcrossSlugChangesTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class JobsAcrossSlugChangesTests
{
    private static readonly Guid RefA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid RefB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static RegistryContext Seeded() => TestDb.CreateContext<RegistryContext>(context =>
    {
        var acme = Seed.Organization(context, "acme", ownerId: "admin-id");
        Seed.Dataset(context, acme, "alpha", RefA);
        Seed.Dataset(context, acme, "beta", RefB);
        Seed.Organization(context, "dest", ownerId: "admin-id");
        context.JobIndices.AddRange(
            Job("a-running", "acme", "alpha", "Processing"), Job("a-old", "acme", "alpha", "Succeeded"),
            Job("b-old", "acme", "beta", "Succeeded"), Job("b-failed", "acme", "beta", "Failed"));
        context.SaveChanges();
    });

    private static JobIndex Job(string id, string org, string dataset, string state) => new()
    {
        JobId = id, OrgSlug = org, DsSlug = dataset, CurrentState = state, CreatedAtUtc = DateTime.UtcNow
    };

    private static (string Job, string Org, string Dataset)[] Jobs(RegistryContext context) =>
        context.JobIndices.AsNoTracking().OrderBy(j => j.JobId).ToArray().Select(j => (j.JobId, j.OrgSlug, j.DsSlug))
            .ToArray();

    private static DatasetCleanupService Cleanup(RegistryContext context, Mock<IBackgroundJobsProcessor> jobs,
        Mock<IDdbManager> ddb) =>
        new(context, ddb.Object, jobs.Object, new JobIndexQuery(context), NullLogger<DatasetCleanupService>.Instance);

    /// <summary>
    /// A is deleted; before its cleanup runs B is renamed to A's slug and enqueues a job there. The cleanup removes A's rows
    /// and cancels A's jobs, and nothing of B (red before: it deleted and cancelled everything under the slug).
    /// </summary>
    [Test]
    public async Task ADeletedDatasetsCleanup_LeavesTheDatasetThatTookItsSlug_Alone()
    {
        await using var context = Seeded();
        var queue = new CleanupJobRecorder();
        var manager = TestDatasets.AdminManager(context, queue.Jobs.Object);

        await manager.Delete("acme", "alpha");

        Jobs(context).Where(j => j.Dataset == "alpha").ShouldBeEmpty("nothing is left under the slug of the deleted dataset");

        await manager.Rename("acme", "beta", "alpha");
        context.JobIndices.Add(Job("b-new", "acme", "alpha", "Processing"));
        await context.SaveChangesAsync();

        var ddb = new Mock<IDdbManager>();
        await queue.RunAsync(Cleanup(context, queue.Jobs, ddb));

        Jobs(context).ShouldBe(
        [
            ("b-failed", "acme", "alpha"), ("b-new", "acme", "alpha"), ("b-old", "acme", "alpha")
        ], "B keeps its history and its running job");
        queue.Jobs.Verify(j => j.Delete("a-running"), Times.Once, "the running job of the deleted dataset is cancelled");
        queue.Jobs.Verify(j => j.Delete(It.Is<string>(id => id != "a-running")), Times.Never);
        ddb.Verify(m => m.Delete("acme", RefA), Times.Once);
        ddb.Verify(m => m.Delete("acme", RefB), Times.Never);
    }

    [Test]
    public async Task ADeletedDatasetsCleanup_AlsoWorksWhenNoOneTookTheSlug()
    {
        await using var context = Seeded();
        var queue = new CleanupJobRecorder();

        await TestDatasets.AdminManager(context, queue.Jobs.Object).Delete("acme", "alpha");
        await queue.RunAsync(Cleanup(context, queue.Jobs, new Mock<IDdbManager>()));

        Jobs(context).ShouldBe([("b-failed", "acme", "beta"), ("b-old", "acme", "beta")]);
        queue.Jobs.Verify(j => j.Delete("a-running"), Times.Once);
    }

    /// <summary>A job queued before the tombstone names the slug: it still runs as it was queued (it cleans by slug).</summary>
    [Test]
    public async Task ACleanupJobRecorderdBeforeTheTombstone_StillCleansBySlug()
    {
        await using var context = Seeded();
        var jobs = new Mock<IBackgroundJobsProcessor>();

        await Cleanup(context, jobs, new Mock<IDdbManager>()).CleanupDeletedDatasetAsync("acme", "alpha", RefA, null);

        Jobs(context).ShouldBe([("b-failed", "acme", "beta"), ("b-old", "acme", "beta")]);
        jobs.Verify(j => j.Delete("a-running"), Times.Once);
    }

    [TestCase("Created")]
    [TestCase("Indexing")]
    [TestCase("Enqueued")]
    [TestCase("Scheduled")]
    [TestCase("Processing")]
    [TestCase("Awaiting")]
    public async Task Rename_WithAJobInState_IsRefused_AndChangesNothing(string state)
    {
        await using var context = Seeded();
        context.JobIndices.Add(Job("b-active", "acme", "beta", state));
        await context.SaveChangesAsync();
        var before = Jobs(context);

        var ex = await Should.ThrowAsync<ConflictException>(() => TestDatasets.AdminManager(context).Rename("acme", "beta", "renamed"));

        ex.Message.ShouldBe(DatasetJobsRule.Message("acme", "beta"));
        ex.Message.ShouldEndWith(DatasetJobsRule.RetryReason);
        context.Datasets.AsNoTracking().Select(d => d.Slug).OrderBy(s => s).ToArray().ShouldBe(["alpha", "beta"]);
        Jobs(context).ShouldBe(before);
    }

    [TestCase("Succeeded")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    public async Task Rename_WithOnlyFinishedJobs_Goes(string state)
    {
        await using var context = Seeded();
        context.JobIndices.Add(Job("b-done", "acme", "beta", state));
        await context.SaveChangesAsync();

        await TestDatasets.AdminManager(context).Rename("acme", "beta", "renamed");

        Jobs(context).Where(j => j.Dataset == "renamed").Select(j => j.Job).ShouldBe(["b-done", "b-failed", "b-old"],
            ignoreOrder: true);
    }

    /// <summary>A running job of another dataset, or of a dataset of another organization with the same slug, does not block.</summary>
    [Test]
    public async Task Rename_IsNotBlockedByTheJobsOfOtherDatasets()
    {
        await using var context = Seeded();
        context.JobIndices.AddRange(Job("other-org", "dest", "beta", "Processing"), Job("a-also", "acme", "alpha", "Enqueued"));
        await context.SaveChangesAsync();

        await TestDatasets.AdminManager(context).Rename("acme", "beta", "renamed");

        context.Datasets.AsNoTracking().Select(d => d.Slug).OrderBy(s => s).ToArray().ShouldBe(["alpha", "renamed"]);
    }

    [Test]
    public async Task Move_WithAJobRunning_IsRefusedForThatDataset_AndChangesNothing()
    {
        await using var context = Seeded();
        var before = Jobs(context);

        // "alpha" has a running job, "beta" has none: the other is moved
        var results = (await TestDatasets.AdminManager(context).MoveToOrganization("acme", ["alpha", "beta"], "dest")).ToArray();

        results.Single(r => r.OriginalSlug == "alpha").Success.ShouldBeFalse();
        results.Single(r => r.OriginalSlug == "alpha").Error.ShouldBe(DatasetJobsRule.Message("acme", "alpha"));
        results.Single(r => r.OriginalSlug == "beta").Success.ShouldBeTrue();
        context.Datasets.AsNoTracking().Include(d => d.Organization).ToArray()
            .Select(d => $"{d.Organization.Slug}/{d.Slug}").Order().ShouldBe(["acme/alpha", "dest/beta"]);
        Jobs(context).ShouldBe(before.Select(j => j.Dataset == "beta" ? (j.Job, "dest", j.Dataset) : j).ToArray(),
            ignoreOrder: true);
    }

    /// <summary>The repair and the rename use the same rule: the states and the texts are the rule's.</summary>
    [Test]
    public void TheRepairAndTheRename_ShareTheRule()
    {
        DatasetJobsRule.RetryReason.ShouldStartWith(DatasetJobsRule.Reason);
        SlugKeyedRows.BlockingJobStates.ShouldBe(["Created", "Indexing", "Enqueued", "Scheduled", "Processing", "Awaiting"]);
    }
}
