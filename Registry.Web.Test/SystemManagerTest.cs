#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using System.Net.Http;
using Hangfire;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

[TestFixture]
public class SystemManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock = null!;
    private Mock<IOptions<AppSettings>> _appSettingsMock = null!;
    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<IObjectsManager> _objectsManagerMock = null!;
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock = null!;
    private Mock<ICacheManager> _cacheManagerMock = null!;
    private ILogger<SystemManager> _logger = null!;
    private AppSettings _settings = null!;

    [SetUp]
    public void Setup()
    {
        _authManagerMock = new Mock<IAuthManager>();
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _objectsManagerMock = new Mock<IObjectsManager>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _cacheManagerMock = new Mock<ICacheManager>();
        _logger = CreateTestLogger<SystemManager>();

        _settings = new AppSettings
        {
            DatasetsPath = "test-datasets"
        };
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);
    }

    private RegistryContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var context = new RegistryContext(options);

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
    public async Task RescanDatasetIndex_NonAdmin_ThrowsUnauthorizedException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);

        var systemManager = CreateSystemManager(context);

        // Act
        Func<Task> act = async () => await systemManager.RescanDatasetIndex("test-org", "test-dataset");

        // Assert
        var ex = await Should.ThrowAsync<UnauthorizedException>(act);
        ex.Message.ShouldContain("Only admins can perform system related tasks");
    }

    [Test]
    public async Task RescanDatasetIndex_Admin_DatasetNotFound_ThrowsArgumentException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var systemManager = CreateSystemManager(context);

        // Act
        Func<Task> act = async () => await systemManager.RescanDatasetIndex("nonexistent-org", "nonexistent-dataset");

        // Assert
        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("Dataset 'nonexistent-org/nonexistent-dataset' not found");
    }

    [Test]
    public async Task RescanDatasetIndex_Admin_ValidDataset_ReturnsResults()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex(It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(new List<RescanResult>
            {
                new RescanResult { Path = "image1.jpg", Success = true, Hash = "abc123" },
                new RescanResult { Path = "image2.jpg", Success = true, Hash = "def456" },
                new RescanResult { Path = "corrupted.jpg", Success = false, Error = "Invalid format" }
            });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var systemManager = CreateSystemManager(context);

        // Act
        var result = await systemManager.RescanDatasetIndex("test-org", "test-dataset");

        // Assert
        result.ShouldNotBeNull();
        result.OrganizationSlug.ShouldBe("test-org");
        result.DatasetSlug.ShouldBe("test-dataset");
        result.TotalProcessed.ShouldBe(3);
        result.SuccessCount.ShouldBe(2);
        result.ErrorCount.ShouldBe(1);
        result.Entries.Count().ShouldBe(3);
    }

    [Test]
    public async Task RescanDatasetIndex_Admin_WithTypesFilter_PassesTypesToDdb()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex("image,geoimage", true))
            .Returns(new List<RescanResult>
            {
                new RescanResult { Path = "image1.jpg", Success = true, Hash = "abc123" }
            });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var systemManager = CreateSystemManager(context);

        // Act
        var result = await systemManager.RescanDatasetIndex("test-org", "test-dataset", "image,geoimage");

        // Assert
        ddbMock.Verify(x => x.RescanIndex("image,geoimage", true), Times.Once);
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task RescanDatasetIndex_EmptyOrgSlug_ThrowsArgumentException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var systemManager = CreateSystemManager(context);

        // Act
        Func<Task> act = async () => await systemManager.RescanDatasetIndex("", "test-dataset");

        // Assert
        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("Organization slug");
    }

    [Test]
    public async Task RescanDatasetIndex_EmptyDsSlug_ThrowsArgumentException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var systemManager = CreateSystemManager(context);

        // Act
        Func<Task> act = async () => await systemManager.RescanDatasetIndex("test-org", "");

        // Assert
        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("Dataset slug");
    }

    private SystemManager CreateSystemManager(RegistryContext context)
    {
        // Create a real BuildPendingService instance with mocked dependencies
        var buildPendingService = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManagerMock.Object,
            Mock.Of<ILogger<BuildPendingService>>());

        return new SystemManager(
            _authManagerMock.Object,
            context,
            _ddbManagerMock.Object,
            _logger,
            _objectsManagerMock.Object,
            _appSettingsMock.Object,
            buildPendingService,
            _httpClientFactoryMock.Object,
            _backgroundJobMock.Object,
            _cacheManagerMock.Object
        );
    }
}

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
    private string _tempDatasetsPath = null!;
    private string _tempPath = null!;

    [SetUp]
    public void Setup()
    {
        _logger = CreateTestLogger<SystemManager>();

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
            cacheManagerMock.Object
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
        result.ImportedItems.Count().ShouldBe(1);
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
        result.ImportedItems.Count().ShouldBe(1);
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

/// <summary>
/// Simple HttpClientFactory for testing that creates real HttpClient instances
/// </summary>
internal class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }
}
