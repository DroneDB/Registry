#nullable enable
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
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using System.Net.Http;
using Hangfire;

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
    private IFileSystem _fileSystem = null!;

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
        _fileSystem = new FileSystem();

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
        ex.Message.ShouldContain("Only admins or users with write access can rescan dataset index");
    }

    [Test]
    public async Task RescanDatasetIndex_UserWithoutWriteAccess_ThrowsUnauthorizedException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Dataset>(), AccessType.Write))
            .ReturnsAsync(false);

        var systemManager = CreateSystemManager(context);

        // Act
        Func<Task> act = async () => await systemManager.RescanDatasetIndex("test-org", "test-dataset");

        // Assert
        var ex = await Should.ThrowAsync<UnauthorizedException>(act);
        ex.Message.ShouldContain("write access");
    }

    [Test]
    public async Task RescanDatasetIndex_Admin_ValidDataset_ReturnsResults()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex(It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns([
                new RescanResult { Path = "image1.jpg", Success = true, Hash = "abc123" },
                new RescanResult { Path = "image2.jpg", Success = true, Hash = "def456" },
                new RescanResult { Path = "corrupted.jpg", Success = false, Error = "Invalid format" }
            ]);

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
        result.Entries.Length.ShouldBe(3);
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
            _cacheManagerMock.Object,
            _fileSystem,
            Mock.Of<IJobIndexWriter>()
        );
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
