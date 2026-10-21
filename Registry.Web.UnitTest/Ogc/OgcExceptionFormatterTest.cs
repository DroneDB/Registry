using System.Xml.Linq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

[TestFixture]
public class OgcExceptionFormatterTest
{
    [Test]
    public void Wms111_Uses_ServiceExceptionReport()
    {
        var xml = OgcExceptionFormatter.FormatWms111("InvalidFormat", "Unsupported");
        var doc = XDocument.Parse(xml);
        doc.Root!.Name.LocalName.ShouldBe("ServiceExceptionReport");
        doc.Root.Attribute("version")!.Value.ShouldBe("1.1.1");
        var ex = doc.Root.Element("ServiceException")!;
        ex.Attribute("code")!.Value.ShouldBe("InvalidFormat");
        ex.Value.ShouldBe("Unsupported");
    }

    [Test]
    public void Ows_Uses_OwsExceptionReport_WithLocator()
    {
        var xml = OgcExceptionFormatter.FormatOws("InvalidParameterValue", "Bad CRS", "1.3.0", "CRS");
        var doc = XDocument.Parse(xml);
        XNamespace ows = "http://www.opengis.net/ows/1.1";
        doc.Root!.Name.ShouldBe(ows + "ExceptionReport");
        doc.Root.Attribute("version")!.Value.ShouldBe("1.3.0");
        var ex = doc.Root.Element(ows + "Exception")!;
        ex.Attribute("exceptionCode")!.Value.ShouldBe("InvalidParameterValue");
        ex.Attribute("locator")!.Value.ShouldBe("CRS");
        ex.Element(ows + "ExceptionText")!.Value.ShouldBe("Bad CRS");
    }

    [Test]
    public void Format_DispatchesByVersion()
    {
        var ex = new OgcException("LayerNotDefined", "layer x", 400, "LAYERS");
        OgcExceptionFormatter.Format(ex, "1.1.1").ShouldContain("ServiceExceptionReport");
        OgcExceptionFormatter.Format(ex, "1.3.0").ShouldContain("ExceptionReport");
        OgcExceptionFormatter.Format(ex, "2.0.0").ShouldContain("ExceptionReport");
    }
}
