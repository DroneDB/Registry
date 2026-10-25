#nullable enable
using Registry.Web.Services.Jobs;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using System.Net.Http;
using Hangfire;
using Registry.Web.Test.Support;
using Registry.Web.Services.Caching;

namespace Registry.Web.Test;

[TestFixture]
public class SystemManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock = null!;
    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock = null!;
    private ILogger<SystemManager> _logger = null!;
    private AppSettings _settings = null!;

    [SetUp]
    public void Setup()
    {
        _authManagerMock = new Mock<IAuthManager>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _logger = CreateTestLogger<SystemManager>();

        _settings = new AppSettings
        {
            DatasetsPath = "test-datasets"
        };
    }

    private RegistryContext CreateInMemoryContext()
    {
        var context = TestDb.CreateContext<RegistryContext>();

        // Seed test data
        var org = new Organization
        {
            Slug = "test-org",
            Name = "Test Organization",
            CreationDate = DateTime.UtcNow,
            OwnerId = "test-user"
        };
        context.Organizations.Add(org);

        var dataset = new Dataset
        {
            Slug = "test-dataset",
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.UtcNow,
            Organization = org
        };
        context.Datasets.Add(dataset);

        context.SaveChanges();
        return context;
    }

    [Test]
    public async Task GetGlobalReport_Admin_ReturnsReportWithOrgsAndDatasets()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.SafeGetCurrentUserName()).ReturnsAsync("admin@test.com");

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Registry.Ports.DroneDB.Entry { Size = 1024L });
        ddbMock.Setup(x => x.FindSummaries(EntryQuery.All)).Returns(new List<Registry.Ports.DroneDB.Entry>
        {
            new Registry.Ports.DroneDB.Entry { Path = "image1.jpg", Size = 100 },
            new Registry.Ports.DroneDB.Entry { Path = "image2.jpg", Size = 200 },
            new Registry.Ports.DroneDB.Entry { Path = "model.obj", Size = 50 }
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Act
        var result = await CreateReport(context).BuildAsync();

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("admin@test.com");
        result.Organizations.Count.ShouldBe(1);
        result.Organizations[0].Name.ShouldBe("Test Organization");
        result.Organizations[0].Slug.ShouldBe("test-org");
        result.Organizations[0].Datasets.Count.ShouldBe(1);
        result.Organizations[0].Datasets[0].Name.ShouldBe("test-dataset");
        result.Organizations[0].Datasets[0].Slug.ShouldBe("test-dataset");
        result.Organizations[0].Datasets[0].Size.ShouldBe(1024L);
        result.Organizations[0].Datasets[0].Contents.Count.ShouldBe(2); // .jpg and .obj
        result.Organizations[0].Datasets[0].Contents[0].Files.ShouldBe(2); // 2 jpg files
        result.Organizations[0].Datasets[0].Contents[0].Size.ShouldBe(300); // 100 + 200
    }

    [Test]
    public async Task GetGlobalReport_DatasetError_IncludesErrorWithoutBreakingRest()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.SafeGetCurrentUserName()).ReturnsAsync("admin@test.com");

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Throws(new InvalidOperationException("Simulated failure"));

        // Act
        var result = await CreateReport(context).BuildAsync();

        // Assert
        result.ShouldNotBeNull();
        result.Organizations.Count.ShouldBe(1);
        result.Organizations[0].Datasets.Count.ShouldBe(1);
        result.Organizations[0].Datasets[0].Error.ShouldNotBeNullOrEmpty();
        result.Organizations[0].Datasets[0].Size.ShouldBe(0);
    }

    // -----------------------------------------------------------------------
    // CleanupBuild tests
    // -----------------------------------------------------------------------

    [Test]
    public async Task CleanupBuild_DatasetSlugWithoutOrgSlug_ThrowsArgumentException()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        Func<Task> act = async () => await CreateMaintenance(context).CleanupBuildAsync(
            new Registry.Web.Models.DTO.CleanupBuildRequestDto { DatasetSlug = "test-dataset" });

        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("OrganizationSlug");
    }

    [Test]
    public async Task CleanupBuild_OrgWithoutDatasets_ThrowsArgumentException()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        Func<Task> act = async () => await CreateMaintenance(context).CleanupBuildAsync(
            new Registry.Web.Models.DTO.CleanupBuildRequestDto { OrganizationSlug = "missing-org" });

        await Should.ThrowAsync<ArgumentException>(act);
    }

    [Test]
    public async Task CleanupBuild_MissingDataset_ThrowsArgumentException()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        Func<Task> act = async () => await CreateMaintenance(context).CleanupBuildAsync(
            new Registry.Web.Models.DTO.CleanupBuildRequestDto
            {
                OrganizationSlug = "test-org",
                DatasetSlug = "missing-dataset"
            });

        await Should.ThrowAsync<ArgumentException>(act);
    }

    [Test]
    public async Task CleanupBuild_AllOrgs_EnqueuesJobsAndReturnsAsync()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.GetCurrentUser())
            .ReturnsAsync(new Registry.Web.Identity.Models.User { Id = "user-1" });

        var ddbMock = new Mock<IDDB>();
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        _backgroundJobMock
            .Setup(x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()))
            .Returns("job-id-1");

        var result = await CreateMaintenance(context).CleanupBuildAsync(new Registry.Web.Models.DTO.CleanupBuildRequestDto());

        result.ShouldNotBeNull();
        result.Async.ShouldBeTrue();
        result.JobId.ShouldBe("job-id-1");
        result.Datasets.Count.ShouldBe(0);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()),
            Times.Once);
        ddbMock.Verify(x => x.Cleanup(), Times.Never);
    }

    [Test]
    public async Task CleanupBuild_SingleOrg_EnqueuesJobsAndReturnsAsync()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.GetCurrentUser())
            .ReturnsAsync(new Registry.Web.Identity.Models.User { Id = "user-1" });

        var ddbMock = new Mock<IDDB>();
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        _backgroundJobMock
            .Setup(x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()))
            .Returns("job-id-2");

        var result = await CreateMaintenance(context).CleanupBuildAsync(
            new Registry.Web.Models.DTO.CleanupBuildRequestDto { OrganizationSlug = "test-org" });

        // Even if the org has only one dataset, scope is "all datasets in org" -> async per the documented contract.
        result.Async.ShouldBeTrue();
        result.JobId.ShouldBe("job-id-2");
        ddbMock.Verify(x => x.Cleanup(), Times.Never);
    }

    [Test]
    public async Task CleanupBuild_SingleDataset_RunsSyncAndReturnsResults()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Cleanup()).Returns(new DdbCleanupResult
        {
            Entries = ["missing.jpg"],
            Builds = ["abc123"]
        });
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var result = await CreateMaintenance(context).CleanupBuildAsync(new Registry.Web.Models.DTO.CleanupBuildRequestDto
        {
            OrganizationSlug = "test-org",
            DatasetSlug = "test-dataset"
        });

        result.Async.ShouldBeFalse();
        result.JobId.ShouldBeNull();
        result.Datasets.Count.ShouldBe(1);
        result.Datasets["test-org/test-dataset"].RemovedEntries.ShouldBe(["missing.jpg"]);
        result.Datasets["test-org/test-dataset"].RemovedBuilds.ShouldBe(["abc123"]);
        result.Errors.Count.ShouldBe(0);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CleanupBuild_SingleDataset_DdbThrows_AggregatesErrorPerDataset()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Cleanup()).Throws(new InvalidOperationException("boom"));
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var result = await CreateMaintenance(context).CleanupBuildAsync(new Registry.Web.Models.DTO.CleanupBuildRequestDto
        {
            OrganizationSlug = "test-org",
            DatasetSlug = "test-dataset"
        });

        result.Async.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors["test-org/test-dataset"].ShouldContain("boom");
        result.Datasets.Count.ShouldBe(0);
    }

    [Test]
    public async Task CleanupBuild_AllOrgs_NoDatasets_ReturnsSyncEmptyResult()
    {
        // Empty context (no orgs/datasets seeded)
        await using var context = TestDb.CreateContext<RegistryContext>();

        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.GetCurrentUser())
            .ReturnsAsync(new Registry.Web.Identity.Models.User { Id = "user-1" });

        var result = await CreateMaintenance(context).CleanupBuildAsync(new Registry.Web.Models.DTO.CleanupBuildRequestDto());

        result.ShouldNotBeNull();
        result.Async.ShouldBeFalse();
        result.JobId.ShouldBeNull();
        result.JobIds.ShouldBeEmpty();
        result.Datasets.ShouldBeEmpty();
        result.Errors.ShouldBeEmpty();
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CleanupBuild_AllOrgs_PopulatesJobIdsPerDataset()
    {
        // Seed two datasets across two orgs
        await using var context = TestDb.CreateContext<RegistryContext>();

        var orgA = new Organization { Slug = "org-a", Name = "A", CreationDate = DateTime.UtcNow, OwnerId = "u" };
        var orgB = new Organization { Slug = "org-b", Name = "B", CreationDate = DateTime.UtcNow, OwnerId = "u" };
        context.Organizations.AddRange(orgA, orgB);
        context.Datasets.AddRange(
            new Dataset { Slug = "d1", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = orgA },
            new Dataset { Slug = "d2", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = orgB });
        await context.SaveChangesAsync();

        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.GetCurrentUser())
            .ReturnsAsync(new Registry.Web.Identity.Models.User { Id = "user-1" });

        var ddbMock = new Mock<IDDB>();
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var seq = 0;
        _backgroundJobMock
            .Setup(x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()))
            .Returns(() => $"job-{++seq}");

        var result = await CreateMaintenance(context).CleanupBuildAsync(new Registry.Web.Models.DTO.CleanupBuildRequestDto());

        result.Async.ShouldBeTrue();
        result.JobIds.Count.ShouldBe(2);
        result.JobIds.ShouldContain("job-1");
        result.JobIds.ShouldContain("job-2");
        // JobId is preserved for back-compat as the last enqueued id
        result.JobId.ShouldBe(result.JobIds[^1]);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(), It.IsAny<Registry.Web.Models.IndexPayload>()),
            Times.Exactly(2));
    }

    // Batch times are UTC from the injected clock; the expiry compares UTC with UTC
    [Test]
    public async Task CleanupBatches_ExpiresRunningBatchesByTheClock()
    {
        await using var context = CreateInMemoryContext();
        var time = new FakeTime(new DateTime(2026, 3, 29, 12, 0, 0, DateTimeKind.Utc));
        _settings.UploadBatchTimeout = TimeSpan.FromHours(1);
        var dataset = await context.Datasets.SingleAsync();
        context.Batches.Add(RunningBatch("expired", dataset, time.Now - TimeSpan.FromHours(2)));
        context.Batches.Add(RunningBatch("fresh", dataset, time.Now - TimeSpan.FromMinutes(30)));
        await context.SaveChangesAsync();
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(Mock.Of<IDDB>());

        var result = await CreateSystemManager(context, time).CleanupBatches();

        result.RemovedBatches.ShouldHaveSingleItem().Token.ShouldBe("expired");
        (await context.Batches.SingleAsync()).Token.ShouldBe("fresh");
    }

    private static Batch RunningBatch(string token, Dataset dataset, DateTime lastEntry) => new()
    {
        Token = token, Dataset = dataset, UserName = "u", Start = lastEntry, Status = BatchStatus.Running,
        Entries = [new Registry.Web.Data.Models.Entry { Path = token + ".jpg", Hash = "h", AddedOn = lastEntry }]
    };

    private GlobalReportBuilder CreateReport(RegistryContext context) =>
        new(context, _ddbManagerMock.Object, _authManagerMock.Object, NullLogger<GlobalReportBuilder>.Instance);

    private BuildMaintenance CreateMaintenance(RegistryContext context) =>
        new(context, _ddbManagerMock.Object, _backgroundJobMock.Object,
            new CurrentUserBuildRequester(_authManagerMock.Object), Mock.Of<IJobIndexWriter>(),
            Microsoft.Extensions.Options.Options.Create(new JobScheduleOptions()), NullLogger<BuildMaintenance>.Instance);

    private SystemManager CreateSystemManager(RegistryContext context, TimeProvider? time = null) =>
        new(context, _ddbManagerMock.Object, Mock.Of<IEntryCommandService>(),
            Microsoft.Extensions.Options.Options.Create(new ShareOptions { UploadBatchTimeout = _settings.UploadBatchTimeout }),
            time ?? TimeProvider.System, Mock.Of<IDatasetListingInvalidator>(), _logger);
}
