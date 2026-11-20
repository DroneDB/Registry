using System.Xml.Linq;
using NUnit.Framework;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// Cross-version smoke tests for WCS conformance metadata + exception formatter.
/// Ensures WCS 1.0/1.1/2.0 each produce the right XML envelope.
/// </summary>
[TestFixture]
public class WcsExceptionFormatterTest
{
    [Test]
    public void Wcs10_Uses_ServiceExceptionReport_NoOws()
    {
        var xml = OgcExceptionFormatter.FormatWcs10("InvalidParameterValue", "Bad BBOX");
        var doc = XDocument.Parse(xml);
        XNamespace ogcNs = "http://www.opengis.net/ogc";
        doc.Root!.Name.ShouldBe(ogcNs + "ServiceExceptionReport");
        doc.Root.Attribute("version")!.Value.ShouldBe("1.2.0");
        var ex = doc.Root.Element(ogcNs + "ServiceException")!;
        ex.Attribute("code")!.Value.ShouldBe("InvalidParameterValue");
        ex.Value.ShouldBe("Bad BBOX");
        // WCS 1.0 predates OWS Common; envelope must NOT use ows:* namespace.
        xml.ShouldNotContain("ows:");
    }

    [Test]
    public void Wcs11_UsesOws11_ExceptionReport()
    {
        var xml = OgcExceptionFormatter.FormatOws("NoSuchCoverage", "Coverage 'x' not found", "1.1.1",
            "Identifier", "http://www.opengis.net/ows/1.1");
        var doc = XDocument.Parse(xml);
        XNamespace ows = "http://www.opengis.net/ows/1.1";
        doc.Root!.Name.ShouldBe(ows + "ExceptionReport");
        doc.Root.Attribute("version")!.Value.ShouldBe("1.1.1");
        var ex = doc.Root.Element(ows + "Exception")!;
        ex.Attribute("exceptionCode")!.Value.ShouldBe("NoSuchCoverage");
        ex.Attribute("locator")!.Value.ShouldBe("Identifier");
    }

    [Test]
    public void Wcs20_UsesOws20_ExceptionReport()
    {
        var xml = OgcExceptionFormatter.FormatOws("InvalidParameterValue", "Bad SUBSET", "2.0.1",
            "subset", "http://www.opengis.net/ows/2.0");
        var doc = XDocument.Parse(xml);
        XNamespace ows = "http://www.opengis.net/ows/2.0";
        doc.Root!.Name.ShouldBe(ows + "ExceptionReport");
    }

    [Test]
    public void Conformance_SupportsThreeVersions()
    {
        WcsConformance.SupportedVersions.ShouldContain("1.0.0");
        WcsConformance.SupportedVersions.ShouldContain("1.1.1");
        WcsConformance.SupportedVersions.ShouldContain("2.0.1");
        // Highest first (so SupportedVersions[0] is the default).
        WcsConformance.SupportedVersions[0].ShouldBe("2.0.1");
    }

    [TestCase("GeoTIFF", "image/tiff")]
    [TestCase("PNG", "image/png")]
    [TestCase("JPEG", "image/jpeg")]
    [TestCase("image/tiff", "image/tiff")]
    [TestCase("image/png", "image/png")]
    public void Wcs10_NormalizeFormat(string input, string expected)
    {
        WcsConformance.NormalizeWcs10Format(input).ShouldBe(expected);
    }
}
