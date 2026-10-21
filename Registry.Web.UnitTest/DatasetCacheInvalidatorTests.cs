using Registry.Web.Services.Ports;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="DatasetCacheInvalidator"/> ensuring every dataset cache
/// category and OGC key pattern is invalidated, and that OGC failures are best-effort.
/// </summary>
[TestFixture]
public class DatasetCacheInvalidatorTests
{
    private Mock<ICacheManager> _cacheManager = null!;
    private Mock<IOgcCacheKeys> _ogcKeys = null!;
    private DatasetCacheInvalidator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cacheManager = new Mock<ICacheManager>();
        _ogcKeys = new Mock<IOgcCacheKeys>();

        _sut = new DatasetCacheInvalidator(
            NullLogger<DatasetCacheInvalidator>.Instance, _cacheManager.Object, _ogcKeys.Object);
    }

    [Test]
    public async Task InvalidateAllDatasetCachesAsync_RemovesAllCacheManagerCategories()
    {
        const string org = "acme";
        const string ds = "survey-2024";
        var category = CacheCategories.ForDataset(org, ds);
        var thumbCategory = CacheCategories.ForDatasetThumbnail(org, ds);

        await _sut.InvalidateAllDatasetCachesAsync(org, ds);

        _cacheManager.Verify(c => c.RemoveByCategoryAsync(MagicStrings.TileCacheSeed, category), Times.Once);
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, category), Times.Once);
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(MagicStrings.BuildPendingTrackerCacheSeed, category), Times.Once);
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, thumbCategory), Times.Once);
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(MagicStrings.OgcRenderCacheSeed, category), Times.Once);

        // Exactly five category removals (tile, thumb, ogc-render, build-pending, ds-thumb).
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(5));
    }

    [Test]
    public async Task InvalidateAllDatasetCachesAsync_RenewsTheOgcGeneration()
    {
        const string org = "acme";
        const string ds = "survey-2024";

        await _sut.InvalidateAllDatasetCachesAsync(org, ds);

        _ogcKeys.Verify(k => k.InvalidateAsync(org, ds), Times.Once);
    }

    [Test]
    public async Task InvalidateAllDatasetCachesAsync_OgcCacheThrows_IsSwallowed()
    {
        _ogcKeys.Setup(k => k.InvalidateAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("cache backend unavailable"));

        await Should.NotThrowAsync(() => _sut.InvalidateAllDatasetCachesAsync("org", "ds"));

        // The category removals run before the OGC step, so they must still have happened.
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(5));
    }
}
