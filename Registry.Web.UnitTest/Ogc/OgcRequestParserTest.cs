using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

[TestFixture]
public class OgcRequestParserTest
{
    private static IQueryCollection Q(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) dict[k] = v;
        return new QueryCollection(dict);
    }

    [Test]
    public void Get_CaseInsensitive()
    {
        var q = Q(("Service", "WMS"));
        OgcRequestParser.Get(q, "service").ShouldBe("WMS");
        OgcRequestParser.Get(q, "SERVICE").ShouldBe("WMS");
        OgcRequestParser.Get(q, "missing").ShouldBeNull();
    }

    [Test]
    public void GetAny_ReturnsFirstAlias()
    {
        var q = Q(("typeNames", "layer1"));
        OgcRequestParser.GetAny(q, "typeName", "typeNames").ShouldBe("layer1");
        OgcRequestParser.GetAny(q, "typeNames", "typeName").ShouldBe("layer1");
    }

    [Test]
    public void GetRequired_Throws_OnMissing()
    {
        var q = Q();
        Should.Throw<OgcException>(() => OgcRequestParser.GetRequired(q, "VERSION"));
    }

    [Test]
    public void GetInt_Defaults_And_Clamps()
    {
        var q = Q(("count", "9999"));
        OgcRequestParser.GetInt(q, "count", 100, 1, 1000).ShouldBe(1000);
        OgcRequestParser.GetInt(q, "missing", 42, 0, 1000).ShouldBe(42);
    }

    [Test]
    public void GetInt_Invalid_Throws()
    {
        var q = Q(("count", "abc"));
        Should.Throw<OgcException>(() => OgcRequestParser.GetInt(q, "count", 0));
    }

    [Test]
    public void ParseBbox_Wms130_Geographic_SwapsAxes()
    {
        // WMS 1.3.0 EPSG:4326 = lat,lon
        var (bbox, crs) = OgcRequestParser.ParseBbox("44.0,9.0,45.0,10.0", "EPSG:4326", "1.3.0");
        crs.ShouldBe("EPSG:4326");
        bbox[0].ShouldBe(9.0);
        bbox[1].ShouldBe(44.0);
        bbox[2].ShouldBe(10.0);
        bbox[3].ShouldBe(45.0);
    }

    [Test]
    public void ParseBbox_Wms111_NoSwap()
    {
        var (bbox, _) = OgcRequestParser.ParseBbox("9.0,44.0,10.0,45.0", "EPSG:4326", "1.1.1");
        bbox[0].ShouldBe(9.0);
        bbox[1].ShouldBe(44.0);
    }

    [Test]
    public void ParseBbox_Crs84_NeverSwapped()
    {
        var (bbox, crs) = OgcRequestParser.ParseBbox("9.0,44.0,10.0,45.0", "CRS:84", "1.3.0");
        crs.ShouldBe("CRS:84");
        bbox[0].ShouldBe(9.0);
        bbox[1].ShouldBe(44.0);
    }

    [Test]
    public void ParseBbox_FifthValueIsCrs()
    {
        var (_, crs) = OgcRequestParser.ParseBbox("9,44,10,45,EPSG:3857", null, "1.3.0");
        crs.ShouldBe("EPSG:3857");
    }

    [Test]
    public void ParseBbox_Invalid_Throws()
    {
        Should.Throw<OgcException>(() => OgcRequestParser.ParseBbox("9,44,10", null, "1.3.0"));
        Should.Throw<OgcException>(() => OgcRequestParser.ParseBbox("a,b,c,d", null, "1.3.0"));
        Should.Throw<OgcException>(() => OgcRequestParser.ParseBbox("10,45,9,44", null, "1.1.1"));
    }

    [Test]
    public void NegotiateWmsVersion()
    {
        OgcRequestParser.NegotiateWmsVersion(null).ShouldBe("1.3.0");
        OgcRequestParser.NegotiateWmsVersion("").ShouldBe("1.3.0");
        OgcRequestParser.NegotiateWmsVersion("1.1.1").ShouldBe("1.1.1");
        OgcRequestParser.NegotiateWmsVersion("1.3.0").ShouldBe("1.3.0");
        OgcRequestParser.NegotiateWmsVersion("2.0.0").ShouldBe("1.3.0");
    }

    [Test]
    public void IsGeographicCrs_Cases()
    {
        OgcRequestParser.IsGeographicCrs("EPSG:4326").ShouldBeTrue();
        OgcRequestParser.IsGeographicCrs("urn:ogc:def:crs:EPSG::4326").ShouldBeTrue();
        OgcRequestParser.IsGeographicCrs("CRS:84").ShouldBeFalse();
        OgcRequestParser.IsGeographicCrs("EPSG:3857").ShouldBeFalse();
        OgcRequestParser.IsGeographicCrs("").ShouldBeFalse();
    }

    [Test]
    public void GetList_Cases()
    {
        var q = Q(("layers", " a , b , ,c "));
        var l = OgcRequestParser.GetList(q, "layers");
        l.ShouldNotBeNull();
        l!.Length.ShouldBe(3);
        l[0].ShouldBe("a");
        l[1].ShouldBe("b");
        l[2].ShouldBe("c");
    }
}
