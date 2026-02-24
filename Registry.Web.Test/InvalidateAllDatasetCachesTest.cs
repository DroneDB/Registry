using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Adapters;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for ObjectsManager.InvalidateAllDatasetCaches to ensure
/// all cache categories are correctly invalidated.
/// </summary>
[TestFixture]
public class InvalidateAllDatasetCachesTest : TestBase
{
    private Mock<ICacheManager> _cacheManagerMock;
    private ObjectsManager _objectsManager;

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper(true);

    [SetUp]
    public void Setup()
    {
        _cacheManagerMock = new Mock<ICacheManager>();

        // Setup all necessary dependencies with mocks
        var appSettingsMock = new Mock<IOptions<AppSettings>>();
        appSettingsMock.Setup(o => o.Value).Returns(new AppSettings());

        var ddbFactoryMock = new Mock<IDdbManager>();
        var authManagerMock = new Mock<IAuthManager>();
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        var thumbnailGeneratorMock = new Mock<IThumbnailGenerator>();
        var jobIndexQueryMock = new Mock<IJobIndexQuery>();
        var backgroundJobsProcessor = new SimpleBackgroundJobsProcessor();
        var fileSystem = new FileSystem();

        var dbOptions = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("InvalidateCacheTest_" + Guid.NewGuid())
            .Options;
        var context = new RegistryContext(dbOptions);

        var webUtils = new WebUtils(authManagerMock.Object, context, appSettingsMock.Object,
            httpContextAccessorMock.Object, ddbFactoryMock.Object);

        var buildPendingService = new BuildPendingService(
            context, ddbFactoryMock.Object, backgroundJobsProcessor, _cacheManagerMock.Object,
            Mock.Of<ILogger<BuildPendingService>>());

        _objectsManager = new ObjectsManager(
            CreateTestLogger<ObjectsManager>(),
            context,
            appSettingsMock.Object,
            ddbFactoryMock.Object,
            webUtils,
            authManagerMock.Object,
            _cacheManagerMock.Object,
            fileSystem,
            backgroundJobsProcessor,
            DdbWrapper,
            thumbnailGeneratorMock.Object,
            jobIndexQueryMock.Object,
            buildPendingService);
    }

    [Test]
    public async Task InvalidateAllDatasetCaches_CallsRemoveByCategoryForAllSeeds()
    {
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var expectedCategory = CacheCategories.ForDataset(orgSlug, dsSlug);
        var expectedThumbCategory = CacheCategories.ForDatasetThumbnail(orgSlug, dsSlug);

        await _objectsManager.InvalidateAllDatasetCaches(orgSlug, dsSlug);

        // Verify tile cache invalidation
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(MagicStrings.TileCacheSeed, expectedCategory),
            Times.Once);

        // Verify thumbnail cache invalidation (per-file)
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, expectedCategory),
            Times.Once);

        // Verify build-pending cache invalidation
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(MagicStrings.BuildPendingTrackerCacheSeed, expectedCategory),
            Times.Once);

        // Verify dataset-level thumbnail cache invalidation (different category)
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, expectedThumbCategory),
            Times.Once);
    }

    [Test]
    public async Task InvalidateAllDatasetCaches_UsesCorrectCategoryFormats()
    {
        const string orgSlug = "acme";
        const string dsSlug = "survey-2024";

        await _objectsManager.InvalidateAllDatasetCaches(orgSlug, dsSlug);

        // Verify the category string format is correct
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(It.IsAny<string>(), "acme/survey-2024"),
            Times.Exactly(3)); // tile, thumb, build-pending all use this

        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(It.IsAny<string>(), "acme/survey-2024/ds-thumb"),
            Times.Once); // dataset thumbnail uses ds-thumb suffix
    }

    [Test]
    public async Task InvalidateAllDatasetCaches_TotalCallsAreFour()
    {
        await _objectsManager.InvalidateAllDatasetCaches("org", "ds");

        // Total: tile + thumb + build-pending + ds-thumb = 4 calls
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(4));
    }
}
