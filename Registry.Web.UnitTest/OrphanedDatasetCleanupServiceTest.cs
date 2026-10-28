#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
public class OrphanedDatasetCleanupServiceTest : TestBase
{
    private MutableOptions<AppSettings> _settingsOptions;
    private ILogger<OrphanedDatasetCleanupService> _logger;
    private string _testDatasetsPath;

    [SetUp]
    public void Setup()
    {
        _settingsOptions = TestSettings.Mutable();
        _logger = CreateTestLogger<OrphanedDatasetCleanupService>();
        _testDatasetsPath = Path.Combine(Path.GetTempPath(), "orphaned-cleanup-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDatasetsPath);
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up test directories
        if (Directory.Exists(_testDatasetsPath))
        {
            try
            {
                Directory.Delete(_testDatasetsPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_NoOrphans_DoesNothing()
    {
        // Arrange
        const string orgSlug = "test-org";
        var internalRef = Guid.NewGuid();

        // Create folder structure
        var orgPath = Path.Combine(_testDatasetsPath, orgSlug);
        var datasetPath = Path.Combine(orgPath, internalRef.ToString());
        Directory.CreateDirectory(datasetPath);

        await using var context = GetContextWithDataset(orgSlug, internalRef);

        _settingsOptions.Value = new AppSettings { DatasetsPath = _testDatasetsPath };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act
        await service.CleanupOrphanedFoldersAsync(null);

        // Assert - Folder should still exist (not orphaned)
        Directory.Exists(datasetPath).ShouldBeTrue();
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_WithOrphan_DeletesOrphanFolder()
    {
        // Arrange
        const string orgSlug = "test-org";
        var validRef = Guid.NewGuid();
        var orphanRef = Guid.NewGuid();

        // Create folder structure
        var orgPath = Path.Combine(_testDatasetsPath, orgSlug);
        var validDatasetPath = Path.Combine(orgPath, validRef.ToString());
        var orphanDatasetPath = Path.Combine(orgPath, orphanRef.ToString());
        Directory.CreateDirectory(validDatasetPath);
        Directory.CreateDirectory(orphanDatasetPath);

        // Create a file in the orphan folder
        File.WriteAllText(Path.Combine(orphanDatasetPath, "test.txt"), "test content");

        // Only the valid dataset is in DB
        await using var context = GetContextWithDataset(orgSlug, validRef);

        _settingsOptions.Value = new AppSettings { DatasetsPath = _testDatasetsPath };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act
        await service.CleanupOrphanedFoldersAsync(null);

        // Assert
        Directory.Exists(validDatasetPath).ShouldBeTrue(); // Valid folder remains
        Directory.Exists(orphanDatasetPath).ShouldBeFalse(); // Orphan folder deleted
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_EmptyDatasetsPath_SkipsCleanup()
    {
        // Arrange
        await using var context = GetEmptyContext();

        _settingsOptions.Value = new AppSettings { DatasetsPath = "" };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(async () => await service.CleanupOrphanedFoldersAsync(null));
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_NonExistentPath_SkipsCleanup()
    {
        // Arrange
        await using var context = GetEmptyContext();

        _settingsOptions.Value = new AppSettings { DatasetsPath = "/non/existent/path" };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(async () => await service.CleanupOrphanedFoldersAsync(null));
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_NonGuidFolder_SkipsFolder()
    {
        // Arrange
        const string orgSlug = "test-org";

        // Create folder structure with non-GUID folder
        var orgPath = Path.Combine(_testDatasetsPath, orgSlug);
        var nonGuidPath = Path.Combine(orgPath, "not-a-guid-folder");
        Directory.CreateDirectory(nonGuidPath);

        await using var context = GetEmptyContext();

        _settingsOptions.Value = new AppSettings { DatasetsPath = _testDatasetsPath };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act
        await service.CleanupOrphanedFoldersAsync(null);

        // Assert - Non-GUID folder should not be deleted
        Directory.Exists(nonGuidPath).ShouldBeTrue();
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_EmptyOrgFolder_DeletesOrgFolder()
    {
        // Arrange
        const string orgSlug = "empty-org";

        // Create empty organization folder
        var orgPath = Path.Combine(_testDatasetsPath, orgSlug);
        Directory.CreateDirectory(orgPath);

        // No datasets in DB for this org
        await using var context = GetEmptyContext();

        _settingsOptions.Value = new AppSettings { DatasetsPath = _testDatasetsPath };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act
        await service.CleanupOrphanedFoldersAsync(null);

        // Assert - Empty org folder should be deleted
        Directory.Exists(orgPath).ShouldBeFalse();
    }

    [Test]
    public async Task CleanupOrphanedFoldersAsync_MultipleOrgsWithOrphans_CleansAllOrphans()
    {
        // Arrange
        const string org1 = "org1";
        const string org2 = "org2";
        var validRef1 = Guid.NewGuid();
        var orphanRef1 = Guid.NewGuid();
        var orphanRef2 = Guid.NewGuid();

        // Create folder structure
        Directory.CreateDirectory(Path.Combine(_testDatasetsPath, org1, validRef1.ToString()));
        Directory.CreateDirectory(Path.Combine(_testDatasetsPath, org1, orphanRef1.ToString()));
        Directory.CreateDirectory(Path.Combine(_testDatasetsPath, org2, orphanRef2.ToString()));

        // Only one dataset is in DB
        await using var context = GetContextWithDataset(org1, validRef1);

        _settingsOptions.Value = new AppSettings { DatasetsPath = _testDatasetsPath };

        var service = new OrphanedDatasetCleanupService(context, TestSettings.Options<StorageOptions>(_settingsOptions), _logger);

        // Act
        await service.CleanupOrphanedFoldersAsync(null);

        // Assert
        Directory.Exists(Path.Combine(_testDatasetsPath, org1, validRef1.ToString())).ShouldBeTrue();
        Directory.Exists(Path.Combine(_testDatasetsPath, org1, orphanRef1.ToString())).ShouldBeFalse();
        Directory.Exists(Path.Combine(_testDatasetsPath, org2, orphanRef2.ToString())).ShouldBeFalse();
        // org2 should also be deleted since it's now empty
        Directory.Exists(Path.Combine(_testDatasetsPath, org2)).ShouldBeFalse();
    }

    private static RegistryContext GetEmptyContext()
    {
        return TestDb.CreateContext<RegistryContext>();
    }

    private static RegistryContext GetContextWithDataset(string orgSlug, Guid internalRef)
    {
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            var org = new Organization
            {
                Slug = orgSlug,
                Name = orgSlug,
                CreationDate = DateTime.Now,
                IsPublic = true
            };

            var ds = new Dataset
            {
                Slug = "test-dataset",
                CreationDate = DateTime.Now,
                InternalRef = internalRef,
                Organization = org
            };

            org.Datasets = new List<Dataset> { ds };
            context.Organizations.Add(org);
            context.SaveChanges();
        });
    }
}
