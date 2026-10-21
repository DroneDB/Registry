using NUnit.Framework;
using Registry.Web.Services;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class CacheCategoriesTest
{
    [Test]
    public void ForDataset_ReturnsExpectedFormat()
    {
        CacheCategories.ForDataset("my-org", "my-ds").ShouldBe("my-org/my-ds");
    }

    [Test]
    public void ForDataset_DifferentSlugs_ReturnsCorrectCombination()
    {
        CacheCategories.ForDataset("acme", "survey-2024").ShouldBe("acme/survey-2024");
    }

    [Test]
    public void ForDatasetThumbnail_ReturnsExpectedFormat()
    {
        CacheCategories.ForDatasetThumbnail("my-org", "my-ds").ShouldBe("my-org/my-ds/ds-thumb");
    }

    [Test]
    public void ForDatasetThumbnail_DifferentSlugs_ReturnsCorrectCombination()
    {
        CacheCategories.ForDatasetThumbnail("acme", "survey-2024").ShouldBe("acme/survey-2024/ds-thumb");
    }

    [Test]
    public void ForDataset_And_ForDatasetThumbnail_SharePrefix()
    {
        const string orgSlug = "org1";
        const string dsSlug = "ds1";

        var datasetCategory = CacheCategories.ForDataset(orgSlug, dsSlug);
        var thumbCategory = CacheCategories.ForDatasetThumbnail(orgSlug, dsSlug);

        thumbCategory.ShouldStartWith(datasetCategory);
    }
}
