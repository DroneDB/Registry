using System;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Test.Support;
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

    [Test]
    public void ForPaths_NormalizesAndDeduplicatesThePaths_AndSelectsMoreThanTheWholeIndex()
    {
        var filter = RescanFilter.ForPaths(TestPaths.Policy, ["a/b.jpg", "/a//b.jpg", "folder\\sub/", "c.tif"]);

        filter.Paths.ShouldBe(["a/b.jpg", "folder/sub", "c.tif"]);
        filter.IsEmpty.ShouldBeFalse("a filter with paths does not select every entry");
        filter.Types.ShouldBeEmpty();
        filter.Extensions.ShouldBeEmpty();
        filter.ToString().ShouldContain("paths: a/b.jpg, folder/sub, c.tif");
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase("/")]
    [TestCase(".")]
    [TestCase("a/../b.jpg")]
    [TestCase(".uploads/x.jpg")]
    public void ForPaths_TheRootAndAPathThatBreaksTheRule_AreRefused(string path)
    {
        Should.Throw<InvalidDatasetPathException>(() => RescanFilter.ForPaths(TestPaths.Policy, ["ok.jpg", path]));
    }

    [Test]
    public void ToOptions_PassesThePaths_AndNoneForAFilterWithout()
    {
        RescanFilter.ForPaths(TestPaths.Policy, ["a.jpg", "dir"]).ToOptions(true).ShouldSatisfyAllConditions(
            o => o.Paths.ShouldBe(["a.jpg", "dir"]),
            o => o.StopOnError.ShouldBeTrue(),
            o => o.Types.ShouldBeNull(),
            o => o.Extensions.ShouldBeNull());

        RescanFilter.Parse("vector", null).ToOptions(false).Paths.ShouldBeNull();
    }

    [TestCase("dir/a.jpg", true)]
    [TestCase("dir/sub/b.jpg", true)]
    [TestCase("single.tif", true)]
    [TestCase("dir2/a.jpg", false)]
    [TestCase("directory.jpg", false)]
    [TestCase("single.tif.bak", false)]
    [TestCase("other/single.tif", false)]
    public void Matches_AFileIsAtOrUnderOneOfThePaths(string path, bool expected)
    {
        var filter = RescanFilter.ForPaths(TestPaths.Policy, ["dir", "single.tif"]);

        filter.Matches(new Entry { Path = path, Type = EntryType.Image }).ShouldBe(expected);
        filter.Matches(new Entry { Path = "dir", Type = EntryType.Directory }).ShouldBeFalse();
    }
}
