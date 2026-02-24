using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class JobIndexCleanupServiceTest : TestBase
{
    private ILogger<JobIndexCleanupService> _cleanupLogger;
    private ILogger<JobIndexWriter> _writerLogger;

    [SetUp]
    public void Setup()
    {
        _cleanupLogger = CreateTestLogger<JobIndexCleanupService>();
        _writerLogger = CreateTestLogger<JobIndexWriter>();
    }

    #region JobIndexWriter.DeleteTerminalBeforeAsync

    [Test]
    public async Task DeleteTerminalBeforeAsync_DeletesSucceededRecordsOlderThanCutoff()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // Act
        var deleted = await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert
        deleted.ShouldBeGreaterThan(0);

        // Old Succeeded should be gone
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldNotContain(j => j.JobId == "old-succeeded");
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_DeletesFailedRecordsOlderThanCutoff()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // Act
        await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldNotContain(j => j.JobId == "old-failed");
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_DeletesDeletedRecordsOlderThanCutoff()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // Act
        await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldNotContain(j => j.JobId == "old-deleted");
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_DoesNotDeleteActiveRecords()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // Act
        await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert - Active jobs (old or new) must survive
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldContain(j => j.JobId == "old-processing");
        remaining.ShouldContain(j => j.JobId == "old-enqueued");
        remaining.ShouldContain(j => j.JobId == "old-created");
        remaining.ShouldContain(j => j.JobId == "old-scheduled");
        remaining.ShouldContain(j => j.JobId == "old-awaiting");
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_DoesNotDeleteRecentTerminalRecords()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // Act
        await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert - Recent terminal records are within retention, must survive
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldContain(j => j.JobId == "recent-succeeded");
        remaining.ShouldContain(j => j.JobId == "recent-failed");
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_ReturnsCorrectCount()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        // Count terminal records that should be deleted
        var expectedDeleted = await context.JobIndices
            .CountAsync(j =>
                (j.CurrentState == "Succeeded" || j.CurrentState == "Failed" || j.CurrentState == "Deleted")
                && j.LastStateChangeUtc < cutoff);

        // Act
        var deleted = await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert
        deleted.ShouldBe(expectedDeleted);
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_EmptyTable_ReturnsZero()
    {
        // Arrange
        await using var context = GetEmptyContext();
        var writer = new JobIndexWriter(context, _writerLogger);

        // Act
        var deleted = await writer.DeleteTerminalBeforeAsync(DateTime.UtcNow);

        // Assert
        deleted.ShouldBe(0);
    }

    [Test]
    public async Task DeleteTerminalBeforeAsync_NoMatchingRecords_ReturnsZero()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);

        // Use a cutoff far in the past so nothing qualifies
        var cutoff = DateTime.UtcNow.AddYears(-10);

        // Act
        var deleted = await writer.DeleteTerminalBeforeAsync(cutoff);

        // Assert
        deleted.ShouldBe(0);
    }

    #endregion

    #region JobIndexCleanupService.CleanupOldJobIndicesAsync

    [Test]
    public async Task CleanupOldJobIndicesAsync_DeletesOldTerminalRecords()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var settings = CreateSettings(retentionDays: 30);
        var service = new JobIndexCleanupService(writer, settings, _cleanupLogger);

        var totalBefore = await context.JobIndices.CountAsync();

        // Act
        await service.CleanupOldJobIndicesAsync();

        // Assert
        var totalAfter = await context.JobIndices.CountAsync();
        totalAfter.ShouldBeLessThan(totalBefore);

        // Old terminal records should be gone
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldNotContain(j => j.JobId == "old-succeeded");
        remaining.ShouldNotContain(j => j.JobId == "old-failed");
        remaining.ShouldNotContain(j => j.JobId == "old-deleted");

        // Active and recent records should remain
        remaining.ShouldContain(j => j.JobId == "old-processing");
        remaining.ShouldContain(j => j.JobId == "recent-succeeded");
    }

    [Test]
    public async Task CleanupOldJobIndicesAsync_UsesConfiguredRetentionDays()
    {
        // Arrange - Use 1 day retention so recent records also get purged
        await using var context = GetContextWithMixedRecords();

        // Add a record that's 2 days old and Succeeded
        context.JobIndices.Add(new JobIndex
        {
            JobId = "two-day-old-succeeded",
            OrgSlug = "test",
            DsSlug = "ds",
            CurrentState = "Succeeded",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
            LastStateChangeUtc = DateTime.UtcNow.AddDays(-2)
        });
        await context.SaveChangesAsync();

        var writer = new JobIndexWriter(context, _writerLogger);
        var settings = CreateSettings(retentionDays: 1);
        var service = new JobIndexCleanupService(writer, settings, _cleanupLogger);

        // Act
        await service.CleanupOldJobIndicesAsync();

        // Assert - The 2-day-old record should be deleted with 1-day retention
        var remaining = await context.JobIndices.ToArrayAsync();
        remaining.ShouldNotContain(j => j.JobId == "two-day-old-succeeded");
    }

    [Test]
    public async Task CleanupOldJobIndicesAsync_ZeroRetentionDays_FallsBackToDefault()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var settings = CreateSettings(retentionDays: 0); // Should fall back to 60
        var service = new JobIndexCleanupService(writer, settings, _cleanupLogger);

        // Act - Should not throw and should use 60-day default
        await Should.NotThrowAsync(async () =>
            await service.CleanupOldJobIndicesAsync());
    }

    [Test]
    public async Task CleanupOldJobIndicesAsync_NegativeRetentionDays_FallsBackToDefault()
    {
        // Arrange
        await using var context = GetContextWithMixedRecords();
        var writer = new JobIndexWriter(context, _writerLogger);
        var settings = CreateSettings(retentionDays: -5); // Should fall back to 60
        var service = new JobIndexCleanupService(writer, settings, _cleanupLogger);

        // Act - Should not throw
        await Should.NotThrowAsync(async () =>
            await service.CleanupOldJobIndicesAsync());
    }

    [Test]
    public async Task CleanupOldJobIndicesAsync_EmptyTable_CompletesWithoutError()
    {
        // Arrange
        await using var context = GetEmptyContext();
        var writer = new JobIndexWriter(context, _writerLogger);
        var settings = CreateSettings(retentionDays: 60);
        var service = new JobIndexCleanupService(writer, settings, _cleanupLogger);

        // Act & Assert
        await Should.NotThrowAsync(async () =>
            await service.CleanupOldJobIndicesAsync());
    }

    #endregion

    #region Helpers

    private static IOptions<AppSettings> CreateSettings(int retentionDays)
    {
        return Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            JobIndexRetentionDays = retentionDays
        });
    }

    private static RegistryContext GetEmptyContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "JobIndexCleanupTestDb_" + Guid.NewGuid())
            .Options;

        return new RegistryContext(options);
    }

    /// <summary>
    /// Creates a context with a mix of old/recent and terminal/active records.
    /// Old = 90 days ago. Recent = 5 days ago.
    /// </summary>
    private static RegistryContext GetContextWithMixedRecords()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "JobIndexCleanupTestDb_" + Guid.NewGuid())
            .Options;

        var oldDate = DateTime.UtcNow.AddDays(-90);
        var recentDate = DateTime.UtcNow.AddDays(-5);

        using (var context = new RegistryContext(options))
        {
            context.JobIndices.AddRange(
                // Old terminal records (should be deleted with 30-day retention)
                new JobIndex { JobId = "old-succeeded", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Succeeded", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },
                new JobIndex { JobId = "old-failed", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Failed", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },
                new JobIndex { JobId = "old-deleted", OrgSlug = "org2", DsSlug = "ds2", CurrentState = "Deleted", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },

                // Old active records (should NOT be deleted)
                new JobIndex { JobId = "old-processing", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Processing", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },
                new JobIndex { JobId = "old-enqueued", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Enqueued", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },
                new JobIndex { JobId = "old-created", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Created", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },
                new JobIndex { JobId = "old-scheduled", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Scheduled", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },
                new JobIndex { JobId = "old-awaiting", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Awaiting", CreatedAtUtc = oldDate.AddHours(-1), LastStateChangeUtc = oldDate },

                // Recent terminal records (should NOT be deleted with 30-day retention)
                new JobIndex { JobId = "recent-succeeded", OrgSlug = "org1", DsSlug = "ds1", CurrentState = "Succeeded", CreatedAtUtc = recentDate.AddHours(-1), LastStateChangeUtc = recentDate },
                new JobIndex { JobId = "recent-failed", OrgSlug = "org2", DsSlug = "ds2", CurrentState = "Failed", CreatedAtUtc = recentDate.AddHours(-1), LastStateChangeUtc = recentDate }
            );
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }

    #endregion
}
