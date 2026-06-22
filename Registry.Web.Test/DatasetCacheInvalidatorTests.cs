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
    private Mock<ICacheKeyScanner> _keyScanner = null!;
    private DatasetCacheInvalidator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cacheManager = new Mock<ICacheManager>();
        _keyScanner = new Mock<ICacheKeyScanner>();
        _keyScanner.Setup(s => s.RemoveByPatternAsync(It.IsAny<string>())).ReturnsAsync(0);

        _sut = new DatasetCacheInvalidator(
            NullLogger<DatasetCacheInvalidator>.Instance, _cacheManager.Object, _keyScanner.Object);
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

        // Exactly four category removals (tile, thumb, build-pending, ds-thumb).
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(4));
    }

    [Test]
    public async Task InvalidateAllDatasetCachesAsync_RemovesOgcKeyPatterns()
    {
        const string org = "acme";
        const string ds = "survey-2024";

        await _sut.InvalidateAllDatasetCachesAsync(org, ds);

        _keyScanner.Verify(s => s.RemoveByPatternAsync(CacheCategories.ForOgcCapabilitiesPattern(org, ds)), Times.Once);
        _keyScanner.Verify(s => s.RemoveByPatternAsync(CacheCategories.ForOgcLayersPattern(org, ds)), Times.Once);
    }

    [Test]
    public async Task InvalidateAllDatasetCachesAsync_OgcScannerThrows_IsSwallowed()
    {
        _keyScanner.Setup(s => s.RemoveByPatternAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("cache backend unavailable"));

        await Should.NotThrowAsync(() => _sut.InvalidateAllDatasetCachesAsync("org", "ds"));

        // The category removals run before the OGC step, so they must still have happened.
        _cacheManager.Verify(c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(4));
    }
}
