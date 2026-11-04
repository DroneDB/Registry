using System.Text.Json;
using NUnit.Framework;
using Registry.Web.Services.Stac;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The bounding boxes of GeoJSON geometries and of STAC extents, and the one rule that flattens a 3D box.</summary>
[TestFixture]
public class GeoJsonBboxTests
{
    private static double[]? Of(string geometry) => GeoJsonBbox.Of(TestJson.Parse(geometry));

    [Test]
    public void APolygon_IsBoundedByItsRings()
    {
        Of("{\"type\":\"Polygon\",\"coordinates\":[[[9,44],[13,44],[13,47],[9,47],[9,44]]]}").ShouldBe([9, 44, 13, 47]);
    }

    [Test]
    public void APointWithAnAltitude_IsABoxOfZeroSize()
    {
        Of("{\"type\":\"Point\",\"coordinates\":[11.5,45.25,310.2]}").ShouldBe([11.5, 45.25, 11.5, 45.25]);
        Of("{\"type\":\"Point\",\"coordinates\":[-3,-4]}").ShouldBe([-3, -4, -3, -4]);
    }

    [Test]
    public void AMultiPoint_AndALineString_AreBoundedByAllTheirPositions()
    {
        Of("{\"type\":\"MultiPoint\",\"coordinates\":[[1,2,9],[5,-6],[3,3]]}").ShouldBe([1, -6, 5, 3]);
        Of("{\"type\":\"LineString\",\"coordinates\":[[1,2],[5,-6],[3,3]]}").ShouldBe([1, -6, 5, 3]);
    }

    [Test]
    public void AMultiPolygon_IsBoundedByAllItsPolygons()
    {
        Of("{\"type\":\"MultiPolygon\",\"coordinates\":[[[[0,0],[1,0],[1,1],[0,0]]],[[[10,10],[12,10],[12,15],[10,10]]]]}")
            .ShouldBe([0, 0, 12, 15]);
    }

    [Test]
    public void AFeature_IsBoundedByItsGeometry()
    {
        Of("{\"type\":\"Feature\",\"properties\":{},\"geometry\":{\"type\":\"Point\",\"coordinates\":[7,8]}}")
            .ShouldBe([7, 8, 7, 8]);
        Of("{\"type\":\"feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[2,0],[2,2],[0,0]]]}}")
            .ShouldBe([0, 0, 2, 2]);
    }

    [TestCase("{\"type\":\"Point\"}")]
    [TestCase("{\"type\":\"Point\",\"coordinates\":[]}")]
    [TestCase("{\"type\":\"Point\",\"coordinates\":[1]}")]
    [TestCase("{\"type\":\"Polygon\",\"coordinates\":[[]]}")]
    [TestCase("{\"type\":\"Polygon\",\"coordinates\":[[[\"a\",\"b\"]]]}")]
    [TestCase("{\"type\":\"Feature\",\"geometry\":null}")]
    [TestCase("[]")]
    [TestCase("null")]
    public void WithoutAValidPosition_ThereIsNoBox(string geometry)
    {
        Of(geometry).ShouldBeNull();
    }

    [Test]
    public void ANullGeometry_HasNoBox()
    {
        GeoJsonBbox.Of(null).ShouldBeNull();
    }

    [Test]
    public void PositionsOutOfTheWorld_AreSkipped()
    {
        // 1e999 does not fit a double; 181 and -91 are not a longitude and a latitude
        Of("{\"type\":\"LineString\",\"coordinates\":[[1,2],[181,0],[0,-91],[1e999,3],[4,5]]}").ShouldBe([1, 2, 4, 5]);
        Of("{\"type\":\"Point\",\"coordinates\":[500000,4600000]}").ShouldBeNull();
    }

    [Test]
    public void NonFiniteCoordinates_AreSkipped()
    {
        var box = new GeoJsonBbox();

        box.Add(double.NaN, 10);
        box.Add(10, double.PositiveInfinity);
        box.Result.ShouldBeNull();

        box.Add(3, 4);
        box.Add(double.NegativeInfinity, 5);
        box.Result.ShouldBe([3, 4, 3, 4]);
    }

    [Test]
    public void TheUnionOfGeometries_IsTheBoxOfAllOfThem()
    {
        var box = new GeoJsonBbox();

        box.Result.ShouldBeNull();
        box.Add(TestJson.Parse("{\"type\":\"Point\",\"coordinates\":[10,20]}"));
        box.Add((JsonElement?)null);
        box.Add(TestJson.Parse("{\"type\":\"Polygon\",\"coordinates\":[[[-5,0],[5,0],[5,3],[-5,0]]]}"));

        box.Result.ShouldBe([-5, 0, 10, 20]);
    }

    [TestCase(new double[] { 0, 0, 0, 0 }, true)]
    [TestCase(new double[] { -180, -90, 180, 90 }, true)]
    [TestCase(new double[] { 10, 20, 30, 40 }, true)]
    [TestCase(new double[] { 30, 20, 10, 40 }, false)]
    [TestCase(new double[] { 10, 20, 30, 91 }, false)]
    [TestCase(new double[] { 10, 20, 181, 40 }, false)]
    [TestCase(new double[] { 10, 20, 30 }, false)]
    public void ABoxOfTheWorld_HasFourValuesInRangeInOrder(double[] bbox, bool within)
    {
        GeoJsonBbox.IsWithinWorld(bbox).ShouldBe(within);
    }

    [Test]
    public void ANullBox_IsNotOfTheWorld()
    {
        GeoJsonBbox.IsWithinWorld(null).ShouldBeFalse();
        GeoJsonBbox.IsWithinWorld([double.NaN, 0, 1, 1]).ShouldBeFalse();
    }

    [TestCase(new double[] { 1, 2, 3, 4 }, new double[] { 1, 2, 3, 4 })]
    [TestCase(new double[] { 1, 2, 9, 3, 4, 10 }, new double[] { 1, 2, 3, 4 })]
    [TestCase(new double[] { 1, 2, 3 }, null)]
    [TestCase(new double[] { }, null)]
    public void FlatteningABox_KeepsTheFootprint(double[] bbox, double[]? flat)
    {
        StacDocuments.FlattenBbox(bbox).ShouldBe(flat);
    }

    [Test]
    public void FlatteningNoBox_GivesNone()
    {
        StacDocuments.FlattenBbox(null).ShouldBeNull();
    }

    [TestCase("{\"extent\":{\"spatial\":{\"bbox\":[[1,2,3,4]]}}}", new double[] { 1, 2, 3, 4 })]
    [TestCase("{\"extent\":{\"spatial\":{\"bbox\":[[1,2,0,3,4,9]]}}}", new double[] { 1, 2, 3, 4 })]
    [TestCase("{\"extent\":{\"spatial\":{\"bbox\":[[1,2,3,4],[5,6,7,8]]}}}", new double[] { 1, 2, 3, 4 })]
    [TestCase("{\"extent\":{\"spatial\":{\"bbox\":[]}}}", null)]
    [TestCase("{\"extent\":{\"spatial\":{\"bbox\":[[1,2,3]]}}}", null)]
    [TestCase("{\"extent\":{\"spatial\":{\"bbox\":[[1,2,3,null]]}}}", null)]
    [TestCase("{\"extent\":{\"spatial\":{}}}", null)]
    [TestCase("{\"extent\":[]}", null)]
    [TestCase("{}", null)]
    [TestCase("[]", null)]
    public void TheSpatialExtentOfACollection_IsItsFirstBoxFlattened(string collection, double[]? expected)
    {
        StacDocuments.SpatialBbox(TestJson.Parse(collection)).ShouldBe(expected);
    }

    [Test]
    public void TheBboxOfAStacQuery_IsTheFootprintInInvariantText()
    {
        StacQueries.BboxToString([1.5, 2, 3, 4.25]).ShouldBe("1.5,2,3,4.25");
        StacQueries.BboxToString([1, 2, 9, 3, 4, 10]).ShouldBe("1,2,3,4");
        StacQueries.BboxToString([1, 2, 3]).ShouldBeNull();
        StacQueries.BboxToString(null).ShouldBeNull();
    }
}
