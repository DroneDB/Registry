#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Integration tests for ImportDataset that test against real remote registry.
/// These tests require network access and should be run explicitly.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("These tests require network access and download real data from hub.dronedb.app")]
public class SystemManagerImportIntegrationTest : TestBase
{
    private const string TestRegistryUrl = "https://hub.dronedb.app";
    private const string TestOrganization = "odm";
    private const string TestDataset = "brighton-beach";

    private ILogger<SystemManager> _logger = null!;
    private IFileSystem _fileSystem = null!;
    private string _tempDatasetsPath = null!;
    private string _tempPath = null!;

    [SetUp]
    public void Setup()
    {
        _logger = CreateTestLogger<SystemManager>();
        _fileSystem = new FileSystem();

        // Create temp directories for test
        _tempDatasetsPath = Path.Combine(Path.GetTempPath(), $"registry-test-{Guid.NewGuid()}");
        _tempPath = Path.Combine(Path.GetTempPath(), $"registry-test-temp-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDatasetsPath);
        Directory.CreateDirectory(_tempPath);
    }

    [TearDown]
    public void TearDown()
    {
        // Cleanup temp directories
        try
        {
            if (Directory.Exists(_tempDatasetsPath))
                Directory.Delete(_tempDatasetsPath, recursive: true);
            if (Directory.Exists(_tempPath))
                Directory.Delete(_tempPath, recursive: true);
        }
        catch (Exception ex)
        {
            TestContext.WriteLine($"Warning: Failed to cleanup temp directories: {ex.Message}");
        }
    }

    private RegistryContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new RegistryContext(options);
    }

    private SystemManager CreateSystemManager(RegistryContext context, Mock<IDdbManager> ddbManagerMock)
    {
        var authManagerMock = new Mock<IAuthManager>();
        authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(new User { Id = "test-user", UserName = "test" });

        var settings = new AppSettings
        {
            DatasetsPath = _tempDatasetsPath,
            TempPath = _tempPath
        };
        var appSettingsMock = new Mock<IOptions<AppSettings>>();
        appSettingsMock.Setup(o => o.Value).Returns(settings);

        var backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        backgroundJobMock.Setup(x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Returns("test-job-id");

        var cacheManagerMock = new Mock<ICacheManager>();
        var objectsManagerMock = new Mock<IObjectsManager>();

        // Use real HttpClientFactory
        var httpClientFactory = new TestHttpClientFactory();

        var buildPendingService = new BuildPendingService(
            context,
            ddbManagerMock.Object,
            backgroundJobMock.Object,
            cacheManagerMock.Object,
            Mock.Of<ILogger<BuildPendingService>>());

        return new SystemManager(
            authManagerMock.Object,
            context,
            ddbManagerMock.Object,
            _logger,
            objectsManagerMock.Object,
            appSettingsMock.Object,
            buildPendingService,
            httpClientFactory,
            backgroundJobMock.Object,
            cacheManagerMock.Object,
            _fileSystem
        );
    }

    [Test]
    public async Task ImportDataset_ArchiveMode_DownloadsAndImportsPublicDataset()
    {
        // Arrange
        await using var context = CreateInMemoryContext();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.DatasetFolderPath).Returns(() =>
            Path.Combine(_tempDatasetsPath, "imported-org", context.Datasets.First().InternalRef.ToString()));

        var ddbManagerMock = new Mock<IDdbManager>();
        ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var systemManager = CreateSystemManager(context, ddbManagerMock);

        var request = new ImportDatasetRequestDto
        {
            SourceRegistryUrl = TestRegistryUrl,
            SourceOrganization = TestOrganization,
            SourceDataset = TestDataset,
            DestinationOrganization = "imported-org",
            DestinationDataset = "imported-dataset",
            Mode = ImportMode.Archive
        };

        // Act
        TestContext.WriteLine($"Starting Archive mode import of {TestOrganization}/{TestDataset}...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await systemManager.ImportDataset(request);

        stopwatch.Stop();
        TestContext.WriteLine($"Import completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");

        // Assert
        result.ShouldNotBeNull();
        result.Errors.ShouldBeEmpty($"Import should succeed without errors. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
        result.ImportedItems.Length.ShouldBe(1);
        result.ImportedItems[0].Organization.ShouldBe("imported-org");
        result.ImportedItems[0].Dataset.ShouldBe("imported-dataset");
        result.TotalFiles.ShouldBeGreaterThan(0);
        result.TotalSize.ShouldBeGreaterThan(0);

        TestContext.WriteLine($"Imported {result.TotalFiles} files, total size: {result.TotalSize / 1024.0 / 1024.0:F2} MB");
        TestContext.WriteLine($"Duration: {result.Duration.TotalSeconds:F2} seconds");
    }

    [Test]
    public async Task ImportDataset_ParallelFilesMode_DownloadsAndImportsPublicDataset()
    {
        // Arrange
        await using var context = CreateInMemoryContext();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.DatasetFolderPath).Returns(() =>
            Path.Combine(_tempDatasetsPath, "imported-org-parallel", context.Datasets.First().InternalRef.ToString()));
        ddbMock.Setup(x => x.GetEntry(It.IsAny<string>())).Returns((Entry?)null); // No existing files
        ddbMock.Setup(x => x.AddRaw(It.IsAny<string>())); // Accept any file addition

        var ddbManagerMock = new Mock<IDdbManager>();
        ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var systemManager = CreateSystemManager(context, ddbManagerMock);

        var request = new ImportDatasetRequestDto
        {
            SourceRegistryUrl = TestRegistryUrl,
            SourceOrganization = TestOrganization,
            SourceDataset = TestDataset,
            DestinationOrganization = "imported-org-parallel",
            DestinationDataset = "imported-dataset-parallel",
            Mode = ImportMode.ParallelFiles
        };

        // Act
        TestContext.WriteLine($"Starting ParallelFiles mode import of {TestOrganization}/{TestDataset}...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await systemManager.ImportDataset(request);

        stopwatch.Stop();
        TestContext.WriteLine($"Import completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds");

        // Assert
        result.ShouldNotBeNull();
        result.Errors.ShouldBeEmpty($"Import should succeed without errors. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
        result.FileErrors.ShouldBeEmpty($"File imports should succeed. File errors: {string.Join(", ", result.FileErrors?.Select(e => $"{e.FilePath}: {e.Message}") ?? Array.Empty<string>())}");
        result.ImportedItems.Length.ShouldBe(1);
        result.ImportedItems[0].Organization.ShouldBe("imported-org-parallel");
        result.ImportedItems[0].Dataset.ShouldBe("imported-dataset-parallel");
        result.TotalFiles.ShouldBeGreaterThan(0);
        result.TotalSize.ShouldBeGreaterThan(0);

        // Verify AddRaw was called for each file
        ddbMock.Verify(x => x.AddRaw(It.IsAny<string>()), Times.AtLeastOnce());

        TestContext.WriteLine($"Imported {result.TotalFiles} files, skipped {result.SkippedFiles} files");
        TestContext.WriteLine($"Total size: {result.TotalSize / 1024.0 / 1024.0:F2} MB");
        TestContext.WriteLine($"Duration: {result.Duration.TotalSeconds:F2} seconds");

        if (result.FileErrors?.Length > 0)
        {
            TestContext.WriteLine($"File errors ({result.FileErrors.Length}):");
            foreach (var error in result.FileErrors)
            {
                TestContext.WriteLine($"  - {error.FilePath}: {error.Message} (retries: {error.RetryCount})");
            }
        }
    }
}