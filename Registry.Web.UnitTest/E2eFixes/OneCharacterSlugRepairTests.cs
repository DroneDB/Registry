using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Maintenance;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// Finding 3, the data: organizations and datasets stored with a one-character slug are unreachable (the routes
/// need two). The slug repair that already renames the slugs the rules refuse (<c>POST /sys/fix-invalid-slugs</c>,
/// <see cref="DatasetSlugRepair"/>) renames them: deterministically (the ToSlug form, padded to the minimum),
/// keeping every slug unique, moving the organization folder, the members and the job records.
/// </summary>
[TestFixture]
public class OneCharacterSlugRepairTests : TestBase
{
    private string _root = null!;
    private Mock<IDatasetListingInvalidator> _listings = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "one-char-slugs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _listings = new Mock<IDatasetListingInvalidator>();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    // "o" and "k" are the one-character organizations; "o0" is taken, so "o" cannot become it
    private RegistryContext Seed(string name)
    {
        var context = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseSqlite("Data Source=" + Path.Combine(_root, name + ".sqlite")).Options);
        context.Database.EnsureCreated();

        Organization Org(string slug, params string[] datasets) => new()
        {
            Slug = slug, Name = slug, CreationDate = DateTime.UtcNow, OwnerId = "owner",
            Datasets = datasets.Select(d => new Dataset { Slug = d, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }).ToList(),
            Users = [new OrganizationUser { OrganizationSlug = slug, UserId = "member", Permissions = OrganizationPermissions.ReadOnly }]
        };

        context.Organizations.AddRange(Org("o", "d", "d0", "ab"), Org("o0", "xx"), Org("k", "z"), Org("fine", "y", "yy"));
        context.JobIndices.Add(new JobIndex
        {
            JobId = "done", OrgSlug = "o", DsSlug = "d", CurrentState = "Succeeded", CreatedAtUtc = DateTime.UtcNow
        });
        context.SaveChanges();

        foreach (var folder in new[] { "o", "k" })
            Directory.CreateDirectory(Path.Combine(DatasetsPath(name), folder));
        File.WriteAllText(Path.Combine(DatasetsPath(name), "o", "marker"), "x");
        return context;
    }

    // Each database has its own datasets folder: a repair moves the organization folders
    private string DatasetsPath(string name) => Path.Combine(_root, name + "-datasets");

    private DatasetSlugRepair Manager(RegistryContext context, string name) =>
        new(context, new Mock<IDdbManager>().Object, new FileSystem(),
            Microsoft.Extensions.Options.Options.Create(new StorageOptions { DatasetsPath = DatasetsPath(name) }),
            _listings.Object, CreateTestLogger<DatasetSlugRepair>());

    [Test]
    public async Task DryRun_PlansTheRenames()
    {
        await using var context = Seed("dry");

        var result = await Manager(context, "dry").FixInvalidSlugsAsync(dryRun: true);

        result.Entries.Select(e => (e.Kind, e.OrganizationSlug, e.OldSlug, e.NewSlug, e.Status)).ShouldBe(
        [
            ("organization", "o", "o", "o0_1", "planned"),
            ("dataset", "o", "d", "d0_1", "planned"),
            ("organization", "k", "k", "k0", "planned"),
            ("dataset", "k", "z", "z0", "planned"),
            ("dataset", "fine", "y", "y0", "planned")
        ], ignoreOrder: true);
        context.Organizations.Select(o => o.Slug).ShouldContain("o");
    }

    [Test]
    public async Task Apply_RenamesTheOneCharacterSlugs_KeepsThemUnique_AndMovesWhatBelongsToThem()
    {
        await using (var context = Seed("apply"))
        {
            var result = await Manager(context, "apply").FixInvalidSlugsAsync(dryRun: false);
            result.Entries.ShouldAllBe(e => e.Status == "renamed");
        }

        await using var check = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseSqlite("Data Source=" + Path.Combine(_root, "apply.sqlite")).Options);

        var orgs = check.Organizations.Select(o => o.Slug).ToArray();
        orgs.ShouldBe(["o0_1", "o0", "k0", "fine"], ignoreOrder: true);
        orgs.ShouldAllBe(s => s.IsValidSlug() && SlugRules.IsWellFormed(s));

        var datasets = check.Datasets.Include(d => d.Organization)
            .Select(d => d.Organization.Slug + "/" + d.Slug).ToArray();
        datasets.ShouldBe(["o0_1/d0_1", "o0_1/d0", "o0_1/ab", "o0/xx", "k0/z0", "fine/y0", "fine/yy"], ignoreOrder: true);
        datasets.Select(d => d.Split('/')[1]).ToArray().ShouldAllBe(s => s.IsValidSlug());

        check.OrganizationsUsers.Where(u => u.OrganizationSlug == "o0_1").Count().ShouldBe(1);
        check.JobIndices.Single(j => j.JobId == "done").ShouldSatisfyAllConditions(
            j => j.OrgSlug.ShouldBe("o0_1"), j => j.DsSlug.ShouldBe("d0_1"));
        File.Exists(Path.Combine(DatasetsPath("apply"), "o0_1", "marker")).ShouldBeTrue();
        Directory.Exists(Path.Combine(DatasetsPath("apply"), "o")).ShouldBeFalse();
        Directory.Exists(Path.Combine(DatasetsPath("apply"), "k0")).ShouldBeTrue();
    }

    [Test]
    public async Task Apply_IsDeterministic_AndRepairedDataNeedsNoSecondRepair()
    {
        string[] Run(string name)
        {
            using var context = Seed(name);
            Manager(context, name).FixInvalidSlugsAsync(dryRun: false).GetAwaiter().GetResult();
            return [.. context.Datasets.Include(d => d.Organization).AsEnumerable()
                .Select(d => d.Organization.Slug + "/" + d.Slug).Order(StringComparer.Ordinal)];
        }

        Run("first").ShouldBe(Run("second"));

        await using var again = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseSqlite("Data Source=" + Path.Combine(_root, "first.sqlite")).Options);
        (await Manager(again, "first").FixInvalidSlugsAsync(dryRun: true)).Entries.ShouldBeEmpty();
    }
}
