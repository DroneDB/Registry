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

    /// <summary>
    /// RQ-12: the distributed-cache keys moved here keep the formats they had where they were written, so the
    /// entries of the previous version stay reachable (and are invalidated) across an upgrade.
    /// </summary>
    [Test]
    public void DistributedCacheKeys_KeepTheirFormats()
    {
        CacheCategories.OgcGeneration("o", "d").ShouldBe("ogc-gen-o-d");
        CacheCategories.OgcDocument("o", "d", "abc", "layers", null).ShouldBe("ogc-layers-o-d-gabc-");
        CacheCategories.OgcDocument("o", "d", "abc", "caps-wfs-v2-2.0.0", "sub").ShouldBe("ogc-caps-wfs-v2-2.0.0-o-d-gabc-sub");
        CacheCategories.OgcRasterProbe("h", "cog").ShouldBe("ogc-wcs-probe-h-cog");
        CacheCategories.DatasetLookup("o", "d").ShouldBe("ds-lookup:o/d");
        CacheCategories.UploadReceipt("o", "d", "u").ShouldBe("upload-receipt-o-d-u");
        CacheCategories.TasksList("o", "d").ShouldBe("tasks-list:o/d");
        CacheCategories.PushSession("/p/session.json").ShouldBe("push-session:/p/session.json");
    }
}
