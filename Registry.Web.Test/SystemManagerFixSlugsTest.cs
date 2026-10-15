#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Common;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Import;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Slugs generated before ToSlug collapsed dashes ("a--b") fail IsValidSlug, which makes
/// those organizations and datasets unreachable. FixInvalidSlugs renames them in the database
/// and on disk.
/// </summary>
[TestFixture("inmemory")]
[TestFixture("sqlite")] // relational: real primary/foreign keys and cascades
public class SystemManagerFixSlugsTest : TestBase
{
    private readonly string _provider;

    public SystemManagerFixSlugsTest(string provider) => _provider = provider;

    private const string BadOrg = "thomas-knig---aerial-data";
    private const string GoodOrg = "valid-org";

    private string _datasetsPath = null!;
    private Mock<IAuthManager> _authManagerMock = null!;
    private Guid _badOrgDsRef;
    private Guid _goodOrgDsRef;

    [SetUp]
    public void Setup()
    {
        _datasetsPath = Path.Combine(Path.GetTempPath(), "fix-slugs-" + Guid.NewGuid());
        Directory.CreateDirectory(_datasetsPath);
        _authManagerMock = new Mock<IAuthManager>();
        _authManagerMock.Setup(o => o.IsUserAdmin()).ReturnsAsync(true);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_datasetsPath))
            Directory.Delete(_datasetsPath, true);
    }

    [Test]
    public async Task DryRun_ReportsPlan_ChangesNothing()
    {
        await using var context = CreateContext();
        var manager = CreateManager(context);

        var result = await manager.FixInvalidSlugs(dryRun: true);

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
            var result = await manager.FixInvalidSlugs(dryRun: false);
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

            var result = await CreateManager(seed).FixInvalidSlugs(dryRun: false);

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
            var result = await CreateManager(seed).FixInvalidSlugs(dryRun: false);

            result.Entries.Where(e => e.OrganizationSlug == BadOrg).ShouldAllBe(e => e.Status == "failed");
        }

        await using var context = ReopenContext();
        context.Organizations.Select(o => o.Slug).ShouldContain(BadOrg);
        context.Datasets.Select(d => d.Slug).ShouldContain("jane--eric");
        File.Exists(Path.Combine(_datasetsPath, BadOrg, _badOrgDsRef.ToString(), "marker")).ShouldBeTrue();
    }

    // Imports write slugs straight to the database: an invalid one must be refused up front
    // (the source address is never contacted, the check runs before any download)
    [TestCase(ImportMode.Archive)]
    [TestCase(ImportMode.ParallelFiles)]
    public async Task ImportDataset_InvalidDestinationSlug_Refused(ImportMode mode)
    {
        await using var context = CreateContext();
        var datasetsBefore = context.Datasets.Count();

        var result = await CreateManager(context).ImportDataset(new ImportDatasetRequestDto
        {
            SourceRegistryUrl = "https://93.184.216.34",
            SourceOrganization = "src",
            SourceDataset = "jake--amy-property",
            DestinationOrganization = GoodOrg,
            Mode = mode
        });

        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("Invalid destination slug");
        context.Datasets.Count().ShouldBe(datasetsBefore);
    }

    [Test]
    public async Task NonAdmin_Throws()
    {
        _authManagerMock.Setup(o => o.IsUserAdmin()).ReturnsAsync(false);
        await using var context = CreateContext();

        await Should.ThrowAsync<UnauthorizedException>(() => CreateManager(context).FixInvalidSlugs());
    }

    #region Setup

    private string _dbName = null!;

    private DbContextOptions<RegistryContext> Options() => _provider == "sqlite"
        ? new DbContextOptionsBuilder<RegistryContext>()
            .UseSqlite("Data Source=" + Path.Combine(_datasetsPath, _dbName + ".sqlite"))
            .Options
        : new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private RegistryContext ReopenContext() => new(Options());

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
        context.SaveChanges();

        var dsFolder = Path.Combine(_datasetsPath, BadOrg, _badOrgDsRef.ToString());
        Directory.CreateDirectory(dsFolder);
        File.WriteAllText(Path.Combine(dsFolder, "marker"), "x");

        return context;
    }

    private SystemManager CreateManager(RegistryContext context)
    {
        var settings = new Mock<IOptions<AppSettings>>();
        settings.Setup(o => o.Value).Returns(new AppSettings { DatasetsPath = _datasetsPath });

        var ddbManager = new Mock<IDdbManager>();
        var backgroundJob = new Mock<IBackgroundJobsProcessor>();
        var cacheManager = new Mock<ICacheManager>();

        var buildPendingService = new BuildPendingService(context, ddbManager.Object, backgroundJob.Object,
            cacheManager.Object, Mock.Of<ILogger<BuildPendingService>>());

        return new SystemManager(_authManagerMock.Object, context, ddbManager.Object,
            CreateTestLogger<SystemManager>(), Mock.Of<IObjectsManager>(), settings.Object, buildPendingService,
            Mock.Of<IHttpClientFactory>(), backgroundJob.Object, cacheManager.Object, new FileSystem(),
            Mock.Of<IJobIndexWriter>(), new SsrfGuard(new ImportSettings()));
    }

    #endregion
}
