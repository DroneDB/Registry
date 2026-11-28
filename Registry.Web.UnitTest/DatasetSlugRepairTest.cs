using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Common;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Maintenance;
using Registry.Web.Utilities;
using Shouldly;
using Registry.Web.Services.Caching;

namespace Registry.Web.Test;

/// <summary>
/// Slugs generated before ToSlug collapsed dashes ("a--b") fail IsValidSlug, which makes
/// those organizations and datasets unreachable. <see cref="DatasetSlugRepair"/> renames
/// them in the database and on disk, and renews the listings that show them.
/// </summary>
[TestFixture("sqlite")] // relational: real primary/foreign keys and cascades
[TestFixture(RetryingProvider)] // retrying execution strategy, as MySQL runs in production
public class DatasetSlugRepairTest : TestBase
{
    private const string RetryingProvider = "sqlite-retrying";

    private readonly string _provider;

    public DatasetSlugRepairTest(string provider) => _provider = provider;

    private const string BadOrg = "thomas-knig---aerial-data";
    private const string GoodOrg = "valid-org";
    private const string ReportTemplates = """[{"id":"standard","name":"Standard","logo":"report-logo-standard.png"}]""";
    private const string ReportLogoFile = "report-logo-standard.png";

    private string _datasetsPath = null!;
    private string _storagePath = null!;
    private Mock<IDatasetListingInvalidator> _listings = null!;
    private Guid _badOrgDsRef;
    private Guid _goodOrgDsRef;

    [SetUp]
    public void Setup()
    {
        _datasetsPath = Path.Combine(Path.GetTempPath(), "fix-slugs-" + Guid.NewGuid());
        Directory.CreateDirectory(_datasetsPath);
        _storagePath = Path.Combine(Path.GetTempPath(), "fix-slugs-storage-" + Guid.NewGuid());
        Directory.CreateDirectory(_storagePath);
        _listings = new Mock<IDatasetListingInvalidator>();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_datasetsPath))
            Directory.Delete(_datasetsPath, true);
        if (Directory.Exists(_storagePath))
            Directory.Delete(_storagePath, true);
    }

    [Test]
    public async Task DryRun_ReportsPlan_ChangesNothing()
    {
        await using var context = CreateContext();
        var manager = CreateManager(context);

        var result = await manager.FixInvalidSlugsAsync(dryRun: true);

        result.DryRun.ShouldBeTrue();
        result.Entries.Select(e => (e.Kind, e.OldSlug, e.NewSlug, e.Status)).ShouldBe(
        [
            ("organization", BadOrg, "thomas-knig-aerial-data", "planned"),
            ("dataset", "jane--eric", "jane-eric", "planned"),
            ("dataset", "a--b", "a-b_1", "planned")
        ], ignoreOrder: true);

        context.Organizations.Select(o => o.Slug).ShouldBe([BadOrg, GoodOrg], ignoreOrder: true);
        context.Datasets.Select(d => d.Slug).ShouldContain("jane--eric");
        Directory.Exists(Path.Combine(_datasetsPath, BadOrg)).ShouldBeTrue();
    }

    [Test]
    public async Task Apply_RenamesOrganizationAndDatasets_MovesFolderUsersAndJobs()
    {
        await using (var seed = CreateContext())
        {
            var manager = CreateManager(seed);
            var result = await manager.FixInvalidSlugsAsync(dryRun: false);
            result.Entries.ShouldAllBe(e => e.Status == "renamed");
        }

        await using var context = ReopenContext();

        context.Organizations.Select(o => o.Slug)
            .ShouldBe(["thomas-knig-aerial-data", GoodOrg], ignoreOrder: true);
        context.Organizations.Select(o => o.Slug).ToArray().ShouldAllBe(s => s.IsValidSlug());
        context.Datasets.Select(d => d.Slug).ToArray().ShouldAllBe(s => s.IsValidSlug());

        var moved = context.Datasets.Include(d => d.Organization).Single(d => d.InternalRef == _badOrgDsRef);
        moved.Slug.ShouldBe("jane-eric");
        moved.Organization.Slug.ShouldBe("thomas-knig-aerial-data");
        moved.Organization.Name.ShouldBe("Thomas König - Aerial Data");

        context.Datasets.Include(d => d.Organization)
            .Count(d => d.Organization.Slug == "thomas-knig-aerial-data").ShouldBe(2);
        context.Datasets.Single(d => d.InternalRef == _goodOrgDsRef).Slug.ShouldBe("a-b_1");

        context.OrganizationsUsers.Select(u => new { u.OrganizationSlug, u.UserId, u.Permissions }).ToArray()
            .ShouldBe([new { OrganizationSlug = "thomas-knig-aerial-data", UserId = "member", Permissions = OrganizationPermissions.ReadOnly }]);

        var job = context.JobIndices.Single(j => j.JobId == "done-job");
        (job.OrgSlug, job.DsSlug).ShouldBe(("thomas-knig-aerial-data", "jane-eric"));

        File.Exists(Path.Combine(_datasetsPath, "thomas-knig-aerial-data", _badOrgDsRef.ToString(), "marker"))
            .ShouldBeTrue();
        Directory.Exists(Path.Combine(_datasetsPath, BadOrg)).ShouldBeFalse();
    }

    [Test]
    public async Task Apply_ActiveJobInOrganization_SkipsIt()
    {
        await using (var seed = CreateContext())
        {
            seed.JobIndices.Add(new JobIndex
            {
                JobId = "running", OrgSlug = BadOrg, DsSlug = "fine", CurrentState = "Processing",
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();

            var result = await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);

            result.Entries.Where(e => e.OrganizationSlug == BadOrg).ShouldAllBe(e => e.Status == "skipped");
            result.Entries.Single(e => e.OrganizationSlug == GoodOrg).Status.ShouldBe("renamed");
        }

        await using var context = ReopenContext();
        context.Organizations.Select(o => o.Slug).ShouldContain(BadOrg);
        Directory.Exists(Path.Combine(_datasetsPath, BadOrg)).ShouldBeTrue();
    }

    [Test]
    public async Task Apply_TargetFolderExists_FailsAndLeavesOrganizationUntouched()
    {
        Directory.CreateDirectory(Path.Combine(_datasetsPath, "thomas-knig-aerial-data"));

        await using (var seed = CreateContext())
        {
            var result = await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);

            result.Entries.Where(e => e.OrganizationSlug == BadOrg).ShouldAllBe(e => e.Status == "failed");
        }

        await using var context = ReopenContext();
        context.Organizations.Select(o => o.Slug).ShouldContain(BadOrg);
        context.Datasets.Select(d => d.Slug).ShouldContain("jane--eric");
        File.Exists(Path.Combine(_datasetsPath, BadOrg, _badOrgDsRef.ToString(), "marker")).ShouldBeTrue();
    }

    // A transient failure rolls the transaction back: the strategy replays the whole rename on a clean slate
    [Test]
    public async Task Apply_TransientFailure_ReplaysRename()
    {
        if (_provider != RetryingProvider) Assert.Ignore("Needs a retrying execution strategy");

        await using (var seed = CreateContext()) { }

        var flaky = new FailFirstSaveOfEachRename();
        await using (var context = ReopenContext(flaky))
        {
            var result = await CreateManager(context).FixInvalidSlugsAsync(dryRun: false);
            result.Entries.ShouldAllBe(e => e.Status == "renamed");
        }

        flaky.Failures.ShouldBe(2); // one per organization

        await using var check = ReopenContext();
        check.Organizations.Select(o => o.Slug).ShouldBe(["thomas-knig-aerial-data", GoodOrg], ignoreOrder: true);
        check.Datasets.Select(d => d.Slug).ToArray()
            .ShouldBe(["jane-eric", "fine", "a-b_1", "a-b"], ignoreOrder: true);
        check.OrganizationsUsers.Select(u => u.OrganizationSlug).ShouldBe(["thomas-knig-aerial-data"]);
        File.Exists(Path.Combine(_datasetsPath, "thomas-knig-aerial-data", _badOrgDsRef.ToString(), "marker"))
            .ShouldBeTrue();
    }

    // The commit reaches the database but reports a transient failure: the rename stands, it is neither
    // replayed nor undone on disk
    [Test]
    public async Task Apply_CommitLandsButReportsFailure_KeepsRename()
    {
        if (_provider != RetryingProvider) Assert.Ignore("Needs a retrying execution strategy");

        await using (var seed = CreateContext()) { }

        var flaky = new FailEveryCommitAfterItLands();
        await using (var context = ReopenContext(flaky))
        {
            var result = await CreateManager(context).FixInvalidSlugsAsync(dryRun: false);
            result.Entries.ShouldAllBe(e => e.Status == "renamed");
        }

        flaky.Failures.ShouldBe(2); // one per organization, none replayed

        await using var check = ReopenContext();
        check.Organizations.Select(o => o.Slug).ShouldBe(["thomas-knig-aerial-data", GoodOrg], ignoreOrder: true);
        check.Datasets.Single(d => d.InternalRef == _goodOrgDsRef).Slug.ShouldBe("a-b_1");
        File.Exists(Path.Combine(_datasetsPath, "thomas-knig-aerial-data", _badOrgDsRef.ToString(), "marker"))
            .ShouldBeTrue();
        Directory.Exists(Path.Combine(_datasetsPath, BadOrg)).ShouldBeFalse();
    }

    // A case-only rename: case-insensitive collations (MySQL) and file systems (Windows, macOS) see the
    // new slug as taken by the old one
    [Test]
    public async Task Apply_CaseOnlyRename_RenamesOrganizationAndFolder()
    {
        var dsRef = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            seed.Organizations.Add(new Organization
            {
                Slug = "Upper-Case", Name = "Upper", CreationDate = DateTime.UtcNow, OwnerId = "owner",
                Datasets = new List<Dataset>
                {
                    new() { Slug = "Upper-Ds", InternalRef = dsRef, CreationDate = DateTime.UtcNow }
                },
                Users = new List<OrganizationUser>
                {
                    new() { OrganizationSlug = "Upper-Case", UserId = "member" }
                }
            });
            await seed.SaveChangesAsync();
            Directory.CreateDirectory(Path.Combine(_datasetsPath, "Upper-Case", dsRef.ToString()));

            var result = await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);

            result.Entries.Where(e => e.OrganizationSlug == "Upper-Case")
                .Select(e => (e.Kind, e.NewSlug, e.Status))
                .ShouldBe([("organization", "upper-case", "renamed"), ("dataset", "upper-ds", "renamed")],
                    ignoreOrder: true);
        }

        await using var context = ReopenContext();
        var slugs = context.Organizations.Select(o => o.Slug).ToArray();
        slugs.ShouldContain("upper-case");
        slugs.ShouldNotContain("Upper-Case");
        context.Datasets.Include(d => d.Organization).Single(d => d.InternalRef == dsRef)
            .ShouldSatisfyAllConditions(
                d => d.Slug.ShouldBe("upper-ds"),
                d => d.Organization.Slug.ShouldBe("upper-case"));
        context.OrganizationsUsers.Where(u => u.UserId == "member").Select(u => u.OrganizationSlug).ToArray()
            .ShouldBe(["thomas-knig-aerial-data", "upper-case"], ignoreOrder: true);

        // Actual on-disk names: Directory.Exists ignores case on Windows and macOS
        Directory.GetDirectories(_datasetsPath).Select(Path.GetFileName).ShouldContain("upper-case");
        Directory.GetDirectories(_datasetsPath).Select(Path.GetFileName).ShouldNotContain("Upper-Case");
        Directory.Exists(Path.Combine(_datasetsPath, "upper-case", dsRef.ToString())).ShouldBeTrue();
    }

    // The STAC listings show the slugs: every dataset whose slug path changed renews them
    [Test]
    public async Task Apply_RenewsTheListingsOfTheRenamedDatasets()
    {
        await using var context = CreateContext();
        var untouched = context.Datasets.Single(d => d.Slug == "a-b").InternalRef;
        var fine = context.Datasets.Single(d => d.Slug == "fine").InternalRef;

        await CreateManager(context).FixInvalidSlugsAsync(dryRun: false);

        // The organization rename moves both of its datasets; in the other organization only "a--b" changed
        _listings.Verify(l => l.ListingChangedAsync(BadOrg, _badOrgDsRef), Times.Once);
        _listings.Verify(l => l.ListingChangedAsync(BadOrg, fine), Times.Once);
        _listings.Verify(l => l.ListingChangedAsync(GoodOrg, _goodOrgDsRef), Times.Once);
        _listings.Verify(l => l.ListingChangedAsync(GoodOrg, untouched), Times.Never);
    }

    // The files of an organization (its logo, the logos of its report templates) are in a folder of the storage path named
    // by its slug, and its report settings are a row keyed by the slug: both follow the organization to its new slug
    [Test]
    public async Task Apply_MovesTheFilesOfTheOrganization_AndKeepsItsReportSettings()
    {
        await using (var seed = CreateContext())
        {
            var result = await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);
            result.Entries.ShouldAllBe(e => e.Status == "renamed");
        }

        var moved = AssetsOf("thomas-knig-aerial-data");
        File.ReadAllText(Path.Combine(moved, "logo")).ShouldBe("the logo");
        File.ReadAllText(Path.Combine(moved, ReportLogoFile)).ShouldBe("the report logo");
        Directory.Exists(AssetsOf(BadOrg)).ShouldBeFalse();

        await using var context = ReopenContext();
        var settings = context.OrganizationReportSettings.AsNoTracking().ToArray();
        settings.Select(r => r.OrganizationSlug).ShouldBe(["thomas-knig-aerial-data"]);
        settings.Single().ShouldSatisfyAllConditions(
            r => r.Templates.ShouldBe(ReportTemplates),
            r => r.Schema.ShouldBe("{}"),
            r => r.Version.ShouldBe(3),
            r => r.UpdatedAt.ShouldBe(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)),
            r => r.UpdatedBy.ShouldBe("owner"));
    }

    [Test]
    public async Task Apply_OrganizationWithoutFiles_IsRenamedAsBefore()
    {
        await using (var seed = CreateContext())
        {
            Directory.Delete(AssetsOf(BadOrg), true);
            seed.OrganizationReportSettings.RemoveRange(seed.OrganizationReportSettings);
            await seed.SaveChangesAsync();

            var result = await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);
            result.Entries.ShouldAllBe(e => e.Status == "renamed");
        }

        Directory.Exists(AssetsOf("thomas-knig-aerial-data")).ShouldBeFalse("nothing was made up");
        await using var context = ReopenContext();
        context.OrganizationReportSettings.Any().ShouldBeFalse();
        context.Organizations.Select(o => o.Slug).ShouldContain("thomas-knig-aerial-data");
    }

    [Test]
    public async Task Apply_FolderOfFilesAlreadyAtTheNewSlug_FailsAndPutsTheDatasetsFolderBack()
    {
        await using (var seed = CreateContext())
        {
            // A folder of the new slug is in the way (made after the seed: the seed has the organization's files at its old slug)
            Directory.CreateDirectory(AssetsOf("thomas-knig-aerial-data"));
            var result = await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);

            result.Entries.Where(e => e.OrganizationSlug == BadOrg).ShouldAllBe(e => e.Status == "failed");
        }

        // Both folders are where they were: the datasets moved first and came back
        File.Exists(Path.Combine(_datasetsPath, BadOrg, _badOrgDsRef.ToString(), "marker")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_datasetsPath, "thomas-knig-aerial-data")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(AssetsOf(BadOrg), "logo")).ShouldBe("the logo");

        await using var context = ReopenContext();
        context.Organizations.Select(o => o.Slug).ShouldContain(BadOrg);
        context.OrganizationReportSettings.Select(r => r.OrganizationSlug).ShouldBe([BadOrg]);
    }

    [Test]
    public async Task Apply_DatabaseFailure_PutsBothFoldersBack()
    {
        await using (var seed = CreateContext()) { }

        await using (var context = ReopenContext(new FailEverySave()))
        {
            var result = await CreateManager(context).FixInvalidSlugsAsync(dryRun: false);

            result.Entries.Where(e => e.OrganizationSlug == BadOrg).ShouldAllBe(e => e.Status == "failed");
        }

        File.Exists(Path.Combine(_datasetsPath, BadOrg, _badOrgDsRef.ToString(), "marker")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(AssetsOf(BadOrg), "logo")).ShouldBe("the logo");
        Directory.Exists(Path.Combine(_datasetsPath, "thomas-knig-aerial-data")).ShouldBeFalse();
        Directory.Exists(AssetsOf("thomas-knig-aerial-data")).ShouldBeFalse();

        await using var check = ReopenContext();
        check.Organizations.Select(o => o.Slug).ShouldContain(BadOrg);
        check.OrganizationReportSettings.Select(r => r.OrganizationSlug).ShouldBe([BadOrg]);
    }

    // The file system and the database of the case-only rename: the files hop through a temporary name as the datasets do
    [Test]
    public async Task Apply_CaseOnlyRename_MovesTheFilesAndTheReportSettings()
    {
        await using (var seed = CreateContext())
        {
            seed.Organizations.Add(new Organization
            {
                Slug = "Upper-Case", Name = "Upper", CreationDate = DateTime.UtcNow, OwnerId = "owner"
            });
            seed.OrganizationReportSettings.Add(new OrganizationReportSettings
            {
                OrganizationSlug = "Upper-Case", Templates = "[]", Schema = "{}", Version = 1,
                UpdatedAt = DateTime.UtcNow, UpdatedBy = "owner"
            });
            await seed.SaveChangesAsync();
            Directory.CreateDirectory(AssetsOf("Upper-Case"));
            File.WriteAllText(Path.Combine(AssetsOf("Upper-Case"), "logo"), "upper logo");

            await CreateManager(seed).FixInvalidSlugsAsync(dryRun: false);
        }

        // Actual on-disk names: Directory.Exists ignores case on Windows and macOS
        var folders = Directory.GetDirectories(Path.Combine(_storagePath, "org-assets")).Select(Path.GetFileName).ToArray();
        folders.ShouldContain("upper-case");
        folders.ShouldNotContain("Upper-Case");
        File.ReadAllText(Path.Combine(AssetsOf("upper-case"), "logo")).ShouldBe("upper logo");

        await using var context = ReopenContext();
        context.OrganizationReportSettings.Select(r => r.OrganizationSlug).ToArray()
            .ShouldBe(["thomas-knig-aerial-data", "upper-case"], ignoreOrder: true);
    }

    [Test]
    public async Task DryRun_RenewsNoListing()
    {
        await using var context = CreateContext();

        await CreateManager(context).FixInvalidSlugsAsync(dryRun: true);

        _listings.VerifyNoOtherCalls();
    }

    private string AssetsOf(string org) => Path.Combine(_storagePath, "org-assets", org);

    #region Setup

    private string _dbName = null!;

    private DbContextOptions<RegistryContext> Options(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<RegistryContext>().AddInterceptors(interceptors);
        // No pooling: an idle pooled connection keeps the file open, and Windows then refuses to delete it
        var sqlitePath = "Data Source=" + Path.Combine(_datasetsPath, _dbName + ".sqlite") + ";Pooling=False";

        return (_provider switch
        {
            "sqlite" => builder.UseSqlite(sqlitePath),
            RetryingProvider => builder.UseSqlite(sqlitePath,
                o => o.ExecutionStrategy(d => new RetryingTestStrategy(d))),
            _ => throw new NotSupportedException($"Unknown test provider '{_provider}'")
        }).Options;
    }

    private RegistryContext ReopenContext(params IInterceptor[] interceptors) => new(Options(interceptors));

    private RegistryContext CreateContext()
    {
        _dbName = Guid.NewGuid().ToString();
        _badOrgDsRef = Guid.NewGuid();
        _goodOrgDsRef = Guid.NewGuid();

        var context = new RegistryContext(Options());
        context.Database.EnsureCreated();

        var bad = new Organization
        {
            Slug = BadOrg, Name = "Thomas König - Aerial Data", CreationDate = DateTime.UtcNow,
            OwnerId = "owner", IsPublic = false,
            Datasets = new List<Dataset>
            {
                new() { Slug = "jane--eric", InternalRef = _badOrgDsRef, CreationDate = DateTime.UtcNow },
                new() { Slug = "fine", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }
            }
        };
        bad.Users = new List<OrganizationUser>
        {
            new()
            {
                Organization = bad, OrganizationSlug = BadOrg, UserId = "member",
                Permissions = OrganizationPermissions.ReadOnly
            }
        };

        var good = new Organization
        {
            Slug = GoodOrg, Name = "Valid", CreationDate = DateTime.UtcNow, OwnerId = "owner",
            Datasets = new List<Dataset>
            {
                new() { Slug = "a--b", InternalRef = _goodOrgDsRef, CreationDate = DateTime.UtcNow },
                new() { Slug = "a-b", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }
            }
        };

        context.Organizations.AddRange(bad, good);
        context.JobIndices.Add(new JobIndex
        {
            JobId = "done-job", OrgSlug = BadOrg, DsSlug = "jane--eric", CurrentState = "Succeeded",
            CreatedAtUtc = DateTime.UtcNow
        });
        context.OrganizationReportSettings.Add(new OrganizationReportSettings
        {
            OrganizationSlug = BadOrg, Templates = ReportTemplates, Schema = "{}", Version = 3,
            UpdatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), UpdatedBy = "owner"
        });
        context.SaveChanges();

        var dsFolder = Path.Combine(_datasetsPath, BadOrg, _badOrgDsRef.ToString());
        Directory.CreateDirectory(dsFolder);
        File.WriteAllText(Path.Combine(dsFolder, "marker"), "x");

        // The files of the organization: its logo, and the logo of a report template
        var assets = AssetsOf(BadOrg);
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "logo"), "the logo");
        File.WriteAllText(Path.Combine(assets, ReportLogoFile), "the report logo");

        return context;
    }

    private DatasetSlugRepair CreateManager(RegistryContext context) =>
        new(context, new Mock<IDdbManager>().Object, new FileSystem(),
            Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { DatasetsPath = _datasetsPath, StoragePath = _storagePath }),
            _listings.Object, CreateTestLogger<DatasetSlugRepair>());

    private sealed class TransientTestException() : Exception("Transient test failure");

    // Behaves like MySqlRetryingExecutionStrategy: refuses user-initiated transactions and replays
    // the operation after a transient failure
    private sealed class RetryingTestStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 3, TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;
    }

    // Fails the first save of every other transaction: each rename fails once, its replay succeeds
    private sealed class FailFirstSaveOfEachRename : IDbTransactionInterceptor, ISaveChangesInterceptor
    {
        private int _transactions;
        private bool _failNextSave;

        public int Failures { get; private set; }

        public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            _failNextSave = _transactions++ % 2 == 0;
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failNextSave) return ValueTask.FromResult(result);

            _failNextSave = false;
            Failures++;
            throw new TransientTestException();
        }
    }

    // A failure the database does not recover from (not transient: nothing replays it)
    private sealed class FailEverySave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The database refuses the save");
    }

    // Every commit reaches the database, then reports a transient failure (a connection lost on the reply)
    private sealed class FailEveryCommitAfterItLands : DbTransactionInterceptor
    {
        public int Failures { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Failures++;
            throw new TransientTestException();
        }
    }

    #endregion
}
