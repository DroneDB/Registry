using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
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
        Should.Throw<OgcException>(() => WmsValidator.ValidateLayers(System.Array.Empty<string>()))
            .Code.ShouldBe("MissingParameterValue");
    }

    [Test]
    public void IsSpectralIndex_RecognizesFiveIndexes()
    {
        WmsValidator.IsSpectralIndex("NDVI").ShouldBeTrue();
        WmsValidator.IsSpectralIndex("ndvi").ShouldBeTrue();
        WmsValidator.IsSpectralIndex(" NDRE ").ShouldBeTrue();
        WmsValidator.IsSpectralIndex("NDWI").ShouldBeTrue();
        WmsValidator.IsSpectralIndex("EVI").ShouldBeTrue();
        WmsValidator.IsSpectralIndex("SAVI").ShouldBeTrue();
        WmsValidator.IsSpectralIndex("default").ShouldBeFalse();
        WmsValidator.IsSpectralIndex(null).ShouldBeFalse();
        WmsValidator.IsSpectralIndex("").ShouldBeFalse();
    }

    [Test]
    public void ValidateStyles_AllowsEmptyAndDefault()
    {
        Should.NotThrow(() => WmsValidator.ValidateStyles(
            new[] { "", "default" }, new[] { "a", "b" }, _ => null));
    }

    [Test]
    public void ValidateStyles_RejectsSpectralIndexOnNonMultispectralLayer()
    {
        var layer = new OgcLayerDto
        {
            Name = "ortho",
            EntryType = EntryType.GeoRaster,
            IsMultispectral = false,
            BandCount = 3
        };
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateStyles(
            new[] { "NDVI" }, new[] { "ortho" }, name => layer));
        ex.Code.ShouldBe("StyleNotDefined");
        ex.Locator.ShouldBe("STYLES");
    }

    [Test]
    public void ValidateStyles_AcceptsSpectralIndexOnMultispectralLayer()
    {
        var layer = new OgcLayerDto
        {
            Name = "msi",
            EntryType = EntryType.GeoRaster,
            IsMultispectral = true,
            BandCount = 5
        };
        Should.NotThrow(() => WmsValidator.ValidateStyles(
            new[] { "NDVI" }, new[] { "msi" }, name => layer));
    }

    [Test]
    public void ValidateStyles_RejectsUnknownStyle()
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateStyles(
            new[] { "thermal" }, new[] { "any" }, _ => null));
        ex.Code.ShouldBe("StyleNotDefined");
    }

    [Test]
    public void ValidateStyles_RejectsSpectralIndexOnUnknownLayer()
    {
        var ex = Should.Throw<OgcException>(() => WmsValidator.ValidateStyles(
            new[] { "NDVI" }, new[] { "missing" }, _ => null));
        ex.Code.ShouldBe("StyleNotDefined");
    }
}
