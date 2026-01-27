using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test;

[TestFixture]
public class DatasetCleanupServiceTest : TestBase
{
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock;
    private Mock<IJobIndexQuery> _jobIndexQueryMock;
    private ILogger<DatasetCleanupService> _logger;

    [SetUp]
    public void Setup()
    {
        _ddbManagerMock = new Mock<IDdbManager>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _jobIndexQueryMock = new Mock<IJobIndexQuery>();
        _logger = CreateTestLogger<DatasetCleanupService>();
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_NoActiveJobs_DeletesFilesystem()
    {
        // Arrange
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var internalRef = Guid.NewGuid();

        await using var context = GetEmptyContext();

        // No active jobs
        _jobIndexQueryMock.Setup(x => x.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue, default))
            .ReturnsAsync(Array.Empty<JobIndex>());

        var service = new DatasetCleanupService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _jobIndexQueryMock.Object,
            _logger
        );

        // Act
        await service.CleanupDeletedDatasetAsync(orgSlug, dsSlug, internalRef, null);

        // Assert - Should delete filesystem
        _ddbManagerMock.Verify(x => x.Delete(orgSlug, internalRef), Times.Once);
        // Should not try to cancel any jobs
        _backgroundJobMock.Verify(x => x.Delete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_WithActiveJobs_CancelsJobsFirst()
    {
        // Arrange
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var internalRef = Guid.NewGuid();

        await using var context = GetEmptyContext();

        // Create some active jobs
        var jobs = new[]
        {
            new JobIndex { JobId = "job-1", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Processing" },
            new JobIndex { JobId = "job-2", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Enqueued" },
            new JobIndex { JobId = "job-3", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Succeeded" } // Not active
        };

        _jobIndexQueryMock.Setup(x => x.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue, default))
            .ReturnsAsync(jobs);

        _backgroundJobMock.Setup(x => x.Delete(It.IsAny<string>())).Returns(true);

        var service = new DatasetCleanupService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _jobIndexQueryMock.Object,
            _logger
        );

        // Act
        await service.CleanupDeletedDatasetAsync(orgSlug, dsSlug, internalRef, null);

        // Assert - Should cancel only active jobs (Processing and Enqueued)
        _backgroundJobMock.Verify(x => x.Delete("job-1"), Times.Once);
        _backgroundJobMock.Verify(x => x.Delete("job-2"), Times.Once);
        _backgroundJobMock.Verify(x => x.Delete("job-3"), Times.Never); // Succeeded is not active
        // Should still delete filesystem
        _ddbManagerMock.Verify(x => x.Delete(orgSlug, internalRef), Times.Once);
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_RemovesJobIndexEntries()
    {
        // Arrange
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var internalRef = Guid.NewGuid();

        await using var context = GetContextWithJobIndices(orgSlug, dsSlug);

        // Verify entries exist before cleanup
        var entriesBefore = await context.JobIndices.CountAsync();
        entriesBefore.ShouldBe(3);

        _jobIndexQueryMock.Setup(x => x.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue, default))
            .ReturnsAsync(Array.Empty<JobIndex>());

        var service = new DatasetCleanupService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _jobIndexQueryMock.Object,
            _logger
        );

        // Act
        await service.CleanupDeletedDatasetAsync(orgSlug, dsSlug, internalRef, null);

        // Assert - All JobIndex entries for this dataset should be removed
        var entriesAfter = await context.JobIndices
            .Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug)
            .CountAsync();
        entriesAfter.ShouldBe(0);
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_FilesystemDeleteFails_DoesNotThrow()
    {
        // Arrange
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var internalRef = Guid.NewGuid();

        await using var context = GetEmptyContext();

        _jobIndexQueryMock.Setup(x => x.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue, default))
            .ReturnsAsync(Array.Empty<JobIndex>());

        // Simulate filesystem delete failure (file locked)
        _ddbManagerMock.Setup(x => x.Delete(orgSlug, internalRef))
            .Throws(new System.IO.IOException("The process cannot access the file because it is being used"));

        var service = new DatasetCleanupService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _jobIndexQueryMock.Object,
            _logger
        );

        // Act & Assert - Should not throw, just log warning
        await Should.NotThrowAsync(async () =>
            await service.CleanupDeletedDatasetAsync(orgSlug, dsSlug, internalRef, null));
    }

    [Test]
    public async Task CleanupDeletedDatasetAsync_JobCancellationFails_ContinuesWithOtherJobs()
    {
        // Arrange
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var internalRef = Guid.NewGuid();

        await using var context = GetEmptyContext();

        var jobs = new[]
        {
            new JobIndex { JobId = "job-1", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Processing" },
            new JobIndex { JobId = "job-2", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Enqueued" }
        };

        _jobIndexQueryMock.Setup(x => x.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue, default))
            .ReturnsAsync(jobs);

        // First job cancellation fails, second succeeds
        _backgroundJobMock.Setup(x => x.Delete("job-1")).Throws(new Exception("Job not found"));
        _backgroundJobMock.Setup(x => x.Delete("job-2")).Returns(true);

        var service = new DatasetCleanupService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _jobIndexQueryMock.Object,
            _logger
        );

        // Act
        await service.CleanupDeletedDatasetAsync(orgSlug, dsSlug, internalRef, null);

        // Assert - Should still try to cancel second job and delete filesystem
        _backgroundJobMock.Verify(x => x.Delete("job-2"), Times.Once);
        _ddbManagerMock.Verify(x => x.Delete(orgSlug, internalRef), Times.Once);
    }

    private RegistryContext GetEmptyContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "CleanupTestDb_" + Guid.NewGuid())
            .Options;

        return new RegistryContext(options);
    }

    private RegistryContext GetContextWithJobIndices(string orgSlug, string dsSlug)
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "CleanupTestDb_" + Guid.NewGuid())
            .Options;

        using (var context = new RegistryContext(options))
        {
            context.JobIndices.AddRange(
                new JobIndex { JobId = "job-1", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Succeeded", CreatedAtUtc = DateTime.UtcNow },
                new JobIndex { JobId = "job-2", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Failed", CreatedAtUtc = DateTime.UtcNow },
                new JobIndex { JobId = "job-3", OrgSlug = orgSlug, DsSlug = dsSlug, CurrentState = "Processing", CreatedAtUtc = DateTime.UtcNow }
            );
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }
}
