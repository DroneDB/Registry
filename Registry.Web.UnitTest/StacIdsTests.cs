using NUnit.Framework;
using Registry.Web.Services.Stac;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The STAC ids Registry gives entries (libddb's data-only documents have none): the STAC API feature
/// id, and the legacy ids and links, which keep libddb's former rules (cases from the contract fixtures recorded
/// with libddb's URL-building export).
/// </summary>
[TestFixture]
public class StacIdsTests
{
    [TestCase("ortho.tif", "path-ortho-tif")]
    [TestCase("sub/img.jpg", "path-sub-img-jpg")]
    [TestCase("DJI_0018.JPG", "path-dji_0018-jpg")]
    [TestCase(".hidden", "path-hidden")]
    [TestCase("a  b--c.TIF", "path-a-b-c-tif")]
    [TestCase("vidéo ü.mp4", "path-vid-o-mp4")]
    [TestCase("end.", "path-end")]
    public void LegacyItemId_IsLibddbsSlugRule(string path, string id)
    {
        StacIds.LegacyItemId(path).ShouldBe(id);
    }

    [TestCase("ortho.tif", "b3J0aG8udGlm")]
    [TestCase("layer.geojson", "bGF5ZXIuZ2VvanNvbg==")]
    [TestCase("sub/img.jpg", "c3ViL2ltZy5qcGc=")]
    public void LegacySegment_IsTheStandardBase64LibddbWrote(string path, string segment)
    {
        StacIds.LegacySegment(path).ShouldBe(segment);
    }

    [Test]
    public void LegacySegment_StandardBase64ThatCannotBeASegment_IsUrlSafe()
    {
        // Standard "fn5+LnRpZg==": '+' (and '/') cannot travel in a route segment as they are
        StacIds.LegacySegment("~~~.tif").ShouldBe("fn5-LnRpZg==");
        StacIds.TryDecodePath(StacIds.LegacySegment("~~~.tif"), out var path).ShouldBeTrue();
        path.ShouldBe("~~~.tif");
    }

    [TestCase("b3J0aG8udGlm", "ortho.tif")]
    [TestCase("bGF5ZXIuZ2VvanNvbg==", "layer.geojson")]
    [TestCase("bGF5ZXIuZ2VvanNvbg", "layer.geojson")]
    [TestCase("fn5+LnRpZg==", "~~~.tif")]
    [TestCase("fn5-LnRpZg", "~~~.tif")]
    public void TryDecodePath_AcceptsBothAlphabets_WithOrWithoutPadding(string segment, string path)
    {
        StacIds.TryDecodePath(segment, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(path);
    }

    [TestCase("")]
    [TestCase("a")]
    [TestCase("!!!!")]
    public void TryDecodePath_NotBase64_IsFalse(string segment)
    {
        StacIds.TryDecodePath(segment, out _).ShouldBeFalse();
    }

    [Test]
    public void FeatureId_IsUrlSafeBase64WithoutPadding()
    {
        StacIds.FeatureId("layer.geojson").ShouldBe("bGF5ZXIuZ2VvanNvbg");
        StacIds.FeatureId("~~~.tif").ShouldBe("fn5-LnRpZg");
    }
}
