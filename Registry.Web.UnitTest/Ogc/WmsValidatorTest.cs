#nullable enable
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

[TestFixture]
public class WmsValidatorTest
{
    [Test]
    public void ValidateCrs_AcceptsWhitelistedSrs()
    {
        Should.NotThrow(() => WmsValidator.ValidateCrs("EPSG:4326"));
        Should.NotThrow(() => WmsValidator.ValidateCrs("EPSG:3857"));
        Should.NotThrow(() => WmsValidator.ValidateCrs("CRS:84"));
        // Case-insensitive
        Should.NotThrow(() => WmsValidator.ValidateCrs("epsg:4326"));
    }

    [Test]
    public void ValidateCrs_RejectsUnknown_WithInvalidCRS()
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateCrs("EPSG:32632"));
        ex.Code.ShouldBe("InvalidCRS");
        ex.Locator.ShouldBe("CRS");
    }

    [Test]
    public void ValidateCrs_RejectsEmpty_WithMissingParameter()
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateCrs(""));
        ex.Code.ShouldBe("MissingParameterValue");
    }

    [Test]
    public void ValidateMapFormat_AcceptsImageFamily()
    {
        Should.NotThrow(() => WmsValidator.ValidateMapFormat("image/png"));
        Should.NotThrow(() => WmsValidator.ValidateMapFormat("image/jpeg"));
        Should.NotThrow(() => WmsValidator.ValidateMapFormat("image/webp"));
    }

    [Test]
    public void ValidateMapFormat_RejectsGif_WithInvalidFormat()
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateMapFormat("image/gif"));
        ex.Code.ShouldBe("InvalidFormat");
        ex.Locator.ShouldBe("FORMAT");
    }

    [Test]
    public void ValidateInfoFormat_AcceptsAllFive()
    {
        foreach (var f in WmsValidator.SupportedInfoFormats)
            Should.NotThrow(() => WmsValidator.ValidateInfoFormat(f));
    }

    [Test]
    public void ValidateInfoFormat_RejectsUnknown()
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateInfoFormat("application/yaml"));
        ex.Code.ShouldBe("InvalidFormat");
        ex.Locator.ShouldBe("INFO_FORMAT");
    }

    [TestCase(0, 256)]
    [TestCase(256, 0)]
    [TestCase(5000, 256)]
    [TestCase(256, 5000)]
    [TestCase(-1, 256)]
    public void ValidateDimensions_RejectsOutOfRange(int w, int h)
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateDimensions(w, h));
        ex.Code.ShouldBe("InvalidParameterValue");
    }

    [Test]
    public void ValidateDimensions_AcceptsBoundary()
    {
        Should.NotThrow(() => WmsValidator.ValidateDimensions(1, 1));
        Should.NotThrow(() => WmsValidator.ValidateDimensions(4096, 4096));
    }

    [Test]
    public void ValidateLayers_RejectsEmptyOrNull()
    {
        Should.Throw<OgcException>(() => WmsValidator.ValidateLayers(null))
            .Code.ShouldBe("MissingParameterValue");
        Should.Throw<OgcException>(() => WmsValidator.ValidateLayers([]))
            .Code.ShouldBe("MissingParameterValue");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("default")]
    [TestCase("DEFAULT")]
    public void IsDefaultStyle_IsTheNaturalColourStyle(string? style)
    {
        WmsValidator.IsDefaultStyle(style).ShouldBeTrue();
    }

    [TestCase("NDVI")]
    [TestCase(" ")]
    [TestCase("thermal")]
    public void IsDefaultStyle_AnyOtherStyleIsNot(string style)
    {
        WmsValidator.IsDefaultStyle(style).ShouldBeFalse();
    }

    [TestCase("NDVI", "NDVI")]
    [TestCase("ndvi", "NDVI")]
    [TestCase("Savi", "SAVI")]
    public void ResolveIndexStyle_AcceptsAnIndexOfTheList_AsLibddbNamesIt(string style, string expected)
    {
        WmsValidator.ResolveIndexStyle(style, "msi", ["NDVI", "NDRE", "NDWI", "SAVI"]).ShouldBe(expected);
    }

    [TestCase("EVI")]
    [TestCase("NDVI")]
    [TestCase("thermal")]
    [TestCase(" NDVI ")]
    public void ResolveIndexStyle_RejectsAnythingNotInTheList(string style)
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ResolveIndexStyle(style, "ortho", ["NDRE", "NDWI"]));

        ex.Code.ShouldBe("StyleNotDefined");
        ex.Locator.ShouldBe("STYLES");
        ex.HttpStatusCode.ShouldBe(400);
        ex.Message.ShouldBe($"Style '{style}' is not defined for layer 'ortho'");
    }

    [Test]
    public void ResolveIndexStyle_ARasterWithNoIndex_HasNoStyleButDefault()
    {
        Should.Throw<OgcException>(() => WmsValidator.ResolveIndexStyle("NDVI", "rgb", []))
            .Code.ShouldBe("StyleNotDefined");
    }
}
