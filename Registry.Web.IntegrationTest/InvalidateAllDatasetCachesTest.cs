using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="DatasetCacheInvalidator"/> (every dataset cache, used by the tools and the
/// rescan) to ensure all cache categories are correctly invalidated.
/// </summary>
[TestFixture]
public class InvalidateAllDatasetCachesTest : TestBase
{
    private Mock<ICacheManager> _cacheManagerMock;
    private DatasetCacheInvalidator _invalidator;

    [SetUp]
    public void Setup()
    {
        _cacheManagerMock = new Mock<ICacheManager>();
        _invalidator = TestJobs.CacheInvalidator(_cacheManagerMock.Object);
    }

    [Test]
    public async Task InvalidateAllDatasetCaches_CallsRemoveByCategoryForAllSeeds()
    {
        const string orgSlug = "test-org";
        const string dsSlug = "test-ds";
        var expectedCategory = CacheCategories.ForDataset(orgSlug, dsSlug);
        var expectedThumbCategory = CacheCategories.ForDatasetThumbnail(orgSlug, dsSlug);

        await _invalidator.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);

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

        // Verify WMS render cache invalidation
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(MagicStrings.OgcRenderCacheSeed, expectedCategory),
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

        await _invalidator.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);

        // Verify the category string format is correct
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(It.IsAny<string>(), "acme/survey-2024"),
            Times.Exactly(4)); // tile, thumb, ogc-render, build-pending all use this

        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(It.IsAny<string>(), "acme/survey-2024/ds-thumb"),
            Times.Once); // dataset thumbnail uses ds-thumb suffix
    }

    [Test]
    public async Task InvalidateAllDatasetCaches_TotalCallsAreFive()
    {
        await _invalidator.InvalidateAllDatasetCachesAsync("org", "ds");

        // Total: tile + thumb + ogc-render + build-pending + ds-thumb = 5 calls
        _cacheManagerMock.Verify(
            c => c.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(5));
    }
}
