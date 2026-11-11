using System;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Services;
using Registry.Web.Services.Caching;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The one rule of which meta key decides who lists a dataset and how (FR-DEV-03, SEC-002):
/// <see cref="IDatasetListingInvalidator.MetaChangedAsync"/>, which the meta API and a push both call. The
/// visibility (any path) drops the cached visibility and the listings, the name of the root drops the cached name and
/// the listings, any other key drops nothing.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetListingInvalidator"
/// </summary>
[TestFixture]
public class DatasetListingInvalidatorTests
{
    private const string Org = "org";
    private static readonly Guid Ref = Guid.Parse("6c1f5555-d001-4411-9308-42aa6ccd7fd6");

    private Mock<ICacheManager> _cache = null!;
    private CacheGenerations _generations = null!;
    private DatasetListingInvalidator _invalidator = null!;

    [SetUp]
    public void Setup()
    {
        _cache = new Mock<ICacheManager>();
        _generations = TestCaches.Generations();
        _invalidator = new DatasetListingInvalidator(_cache.Object, Mock.Of<IDdbManager>(), _generations);
    }

    private void VerifyRemoved(string seed, Times times) =>
        _cache.Verify(c => c.RemoveAsync(seed, Org, It.Is<object[]>(p => p.Length > 1 && Equals(p[1], Ref))), times);

    // The STAC listings are keyed by a generation: a new one means they all went
    private async Task<bool> ListingsRenewedBy(Func<Task> change)
    {
        var before = await _generations.CurrentAsync(CacheCategories.StacGeneration);
        await change();
        return await _generations.CurrentAsync(CacheCategories.StacGeneration) != before;
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("photos/a.jpg")]
    public async Task Visibility_DropsTheCachedVisibility_AndTheListings(string? path)
    {
        (await ListingsRenewedBy(() => _invalidator.MetaChangedAsync(Org, Ref, "visibility", path))).ShouldBeTrue();

        VerifyRemoved(MagicStrings.DatasetVisibilityCacheSeed, Times.Once());
        VerifyRemoved(MagicStrings.DatasetNameCacheSeed, Times.Once());
    }

    // null from the meta API, "" from a meta dump: both are the root
    [TestCase(null)]
    [TestCase("")]
    public async Task RootName_DropsTheCachedName_AndTheListings_NotTheVisibility(string? path)
    {
        (await ListingsRenewedBy(() => _invalidator.MetaChangedAsync(Org, Ref, "name", path))).ShouldBeTrue();

        VerifyRemoved(MagicStrings.DatasetNameCacheSeed, Times.Once());
        VerifyRemoved(MagicStrings.DatasetVisibilityCacheSeed, Times.Never());
    }

    [Test]
    public async Task NameOfAnEntry_DropsNothing()
    {
        (await ListingsRenewedBy(() => _invalidator.MetaChangedAsync(Org, Ref, "name", "photos/a.jpg"))).ShouldBeFalse();

        _cache.VerifyNoOtherCalls();
    }

    [TestCase("annotations", null)]
    [TestCase("tags", "")]
    [TestCase("description", "photos/a.jpg")]
    public async Task OtherKey_DropsNothing(string key, string? path)
    {
        (await ListingsRenewedBy(() => _invalidator.MetaChangedAsync(Org, Ref, key, path))).ShouldBeFalse();

        _cache.VerifyNoOtherCalls();
    }
}
