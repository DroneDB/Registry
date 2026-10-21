#nullable enable
using System;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class RescanFilterTest
{
    [Test]
    public void Parse_NormalizesTypesAndExtensions()
    {
        var filter = RescanFilter.Parse([" Vector", "GEORASTER,vector", ""], ["dwg, .DXF", "copc.LAZ", " "]);

        filter.Types.ShouldBe([EntryType.Vector, EntryType.GeoRaster]);
        filter.Extensions.ShouldBe(["dwg", "dxf", "copc.laz"]);
        filter.TypesArgument.ShouldBe("vector,georaster");
        filter.ExtensionsArgument.ShouldBe("dwg,dxf,copc.laz");
        filter.IsEmpty.ShouldBeFalse();
    }

    [Test]
    public void Parse_TypesArgumentUsesNativeTypeNames()
    {
        // DroneDB type names are the enum names ignoring case (GeoVideo, Tiles3D, ...)
        RescanFilter.Parse("geovideo,tiles3d,gaussiansplat,dronedb", null).TypesArgument
            .ShouldBe("geovideo,tiles3d,gaussiansplat,dronedb");
    }

    [Test]
    public void Parse_NullOrEmpty_IsEmpty()
    {
        RescanFilter.Parse((string?)null, null).IsEmpty.ShouldBeTrue();
        RescanFilter.Parse(" , ", "").IsEmpty.ShouldBeTrue();
        RescanFilter.Parse((string[]?)null, null).TypesArgument.ShouldBeNull();
        RescanFilter.Parse((string[]?)null, null).ExtensionsArgument.ShouldBeNull();
    }

    [TestCase("nope")]
    [TestCase("5")]
    [TestCase("undefined")]
    [TestCase("directory")]
    public void Parse_InvalidType_Throws(string type)
    {
        Should.Throw<ArgumentException>(() => RescanFilter.Parse(type, null));
    }

    [TestCase(".")]
    [TestCase("*.dwg")]
    [TestCase("a/dwg")]
    [TestCase("a\\dwg")]
    [TestCase("tar gz")]
    public void Parse_InvalidExtension_Throws(string extension)
    {
        Should.Throw<ArgumentException>(() => RescanFilter.Parse(null, extension));
    }

    [TestCase("plans/site.DWG", EntryType.Generic, true)]
    [TestCase("plans/site.dxf", EntryType.Vector, true)]
    [TestCase("plans/site.dwg.bak", EntryType.Generic, false)]
    [TestCase("plans/sitedwg", EntryType.Generic, false)]
    [TestCase("dwg", EntryType.Generic, false)]
    [TestCase("plans.dwg", EntryType.Directory, false)]
    public void Matches_Extension(string path, EntryType type, bool expected)
    {
        var filter = RescanFilter.Parse(null, "dwg,dxf");

        filter.Matches(new Entry { Path = path, Type = type }).ShouldBe(expected);
    }

    [Test]
    public void ToEntryQuery_PushesTypesAndASingleAsciiExtension()
    {
        var query = RescanFilter.Parse("vector", "dwg").ToEntryQuery();
        query.Types.ShouldBe([EntryType.Vector]);
        query.NamePattern.ShouldBe("*.dwg");
        query.Recursive.ShouldBeTrue();

        // Several extensions, or one the native pattern would compare case-sensitively: types only
        RescanFilter.Parse("vector", "dwg,dxf").ToEntryQuery().NamePattern.ShouldBeNull();
        RescanFilter.Parse((string?)null, "éxt").ToEntryQuery().NamePattern.ShouldBeNull();
        RescanFilter.Parse((string?)null, "dwg").ToEntryQuery().Types.ShouldBeNull();
    }

    [Test]
    public void Matches_TypeAndExtensionMustBothMatch()
    {
        var filter = RescanFilter.Parse("vector", "dwg");

        filter.Matches(new Entry { Path = "a.dwg", Type = EntryType.Vector }).ShouldBeTrue();
        filter.Matches(new Entry { Path = "a.dwg", Type = EntryType.Generic }).ShouldBeFalse();
        filter.Matches(new Entry { Path = "a.shp", Type = EntryType.Vector }).ShouldBeFalse();
    }

    [Test]
    public void Matches_CompoundExtension()
    {
        var filter = RescanFilter.Parse(null, "copc.laz");

        filter.Matches(new Entry { Path = "scan.copc.laz", Type = EntryType.PointCloud }).ShouldBeTrue();
        filter.Matches(new Entry { Path = "scan.laz", Type = EntryType.PointCloud }).ShouldBeFalse();
    }
}
