#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-15: the job history of a dataset (the <c>JobIndices</c> rows, which name it by organization and dataset slug)
/// follows it when it is renamed or moved to another organization, in the transaction of the rename or move itself.
/// The slug repair did this alone; the rename and the move left the rows under the old slugs, so the dataset lost its
/// history (and a new dataset taking the old slug inherited it). The same scenarios run on MariaDB in
/// <c>Rq15JobHistoryFollowsSlugMariaDbTests</c> (Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq15JobHistoryFollowsSlugTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq15JobHistoryFollowsSlugTests
{
    private static RegistryContext Seeded() => TestDb.CreateContext<RegistryContext>(context =>
    {
        var source = Seed.Organization(context, "source", ownerId: "admin-id");
        Seed.Dataset(context, source, "moving");
        Seed.Dataset(context, source, "staying");
        var other = Seed.Organization(context, "other", ownerId: "admin-id");
        Seed.Dataset(context, other, "moving");
        context.JobIndices.AddRange(Job("a", "source", "moving"), Job("b", "source", "moving"),
            Job("c", "source", "staying"), Job("d", "other", "moving"));
        context.SaveChanges();
    });

    private static JobIndex Job(string id, string org, string dataset) => new()
    {
        // Finished jobs: a dataset with jobs queued or running is not renamed or moved (DatasetJobsRule)
        JobId = id, OrgSlug = org, DsSlug = dataset, CurrentState = "Succeeded", CreatedAtUtc = DateTime.UtcNow
    };

    private static (string Job, string Org, string Dataset)[] Jobs(RegistryContext context) =>
        context.JobIndices.AsNoTracking().OrderBy(j => j.JobId).ToArray().Select(j => (j.JobId, j.OrgSlug, j.DsSlug))
            .ToArray();

    [Test]
    public async Task Rename_TheJobsOfTheDataset_FollowItsSlug()
    {
        await using var context = Seeded();

        await TestDatasets.AdminManager(context).Rename("source", "moving", "renamed");

        Jobs(context).ShouldBe(
        [
            ("a", "source", "renamed"), ("b", "source", "renamed"), ("c", "source", "staying"), ("d", "other", "moving")
        ]);
    }

    [Test]
    public async Task Move_TheJobsOfTheDataset_FollowItToTheOtherOrganization()
    {
        await using var context = Seeded();
        context.Organizations.Add(new Organization
        {
            Slug = "dest", Name = "dest", CreationDate = DateTime.UtcNow, OwnerId = "admin-id"
        });
        await context.SaveChangesAsync();

        var results = (await TestDatasets.AdminManager(context).MoveToOrganization("source", ["moving"], "dest")).ToArray();

        results.ShouldHaveSingleItem().Success.ShouldBeTrue();
        Jobs(context).ShouldBe(
        [
            ("a", "dest", "moving"), ("b", "dest", "moving"), ("c", "source", "staying"), ("d", "other", "moving")
        ]);
    }

    [Test]
    public async Task Move_WithARenamedSlug_TheJobsFollowTheNewSlug()
    {
        await using var context = Seeded();

        // "other" already has a "moving": the Rename strategy gives the moved one "moving_1"
        var results = (await TestDatasets.AdminManager(context)
            .MoveToOrganization("source", ["moving"], "other", ConflictResolutionStrategy.Rename)).ToArray();

        results.ShouldHaveSingleItem().NewSlug.ShouldBe("moving_1");
        Jobs(context).ShouldBe(
        [
            ("a", "other", "moving_1"), ("b", "other", "moving_1"), ("c", "source", "staying"), ("d", "other", "moving")
        ]);
    }

    [Test]
    public async Task Rename_ToATakenSlug_LeavesTheJobsWhereTheyAre()
    {
        await using var context = Seeded();

        await Should.ThrowAsync<Exception>(() => TestDatasets.AdminManager(context).Rename("source", "moving", "staying"));

        Jobs(context).ShouldBe(
        [
            ("a", "source", "moving"), ("b", "source", "moving"), ("c", "source", "staying"), ("d", "other", "moving")
        ]);
    }

    [Test]
    public async Task DatasetThenOrganization_ComposeInOneSave_AsTheRepairUsesThem()
    {
        await using var context = Seeded();

        // The repair renames a dataset of an organization that is renamed too: "moving" -> "fixed" and "source" -> "fixed-org"
        await context.FollowDatasetAsync("source", "moving", "fixed-org", "fixed");
        await context.FollowOrganizationAsync("source", "fixed-org");
        await context.SaveChangesAsync();

        Jobs(context).ShouldBe(
        [
            ("a", "fixed-org", "fixed"), ("b", "fixed-org", "fixed"), ("c", "fixed-org", "staying"),
            ("d", "other", "moving")
        ]);
    }

    /// <summary>
    /// Only the task index names a dataset by its slugs. A table with a slug column that is not in this list needs a rule
    /// in <see cref="SlugKeyedRows"/> (and a place in this list), or its rows lose their dataset on a rename or a move.
    /// </summary>
    [Test]
    public void TheModel_HasNoSlugColumnTheRenameDoesNotKnow()
    {
        using var context = Seeded();

        var slugColumns = context.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Select(p => $"{t.ClrType.Name}.{p.Name}"))
            .Where(c => c.EndsWith("Slug", StringComparison.Ordinal));

        slugColumns.ShouldBe(
        [
            // the keys themselves and the foreign keys that point at them (changed with their row)
            "Organization.Slug", "Dataset.Slug", "Dataset.OrganizationSlug", "OrganizationUser.OrganizationSlug",
            // the rows named by slug, followed by SlugKeyedRows
            "JobIndex.OrgSlug", "JobIndex.DsSlug"
        ], ignoreOrder: true);
    }
}
