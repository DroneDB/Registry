using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// The streaming OGR GML to WFS 2.0 rewrite. The expected files were produced by the DOM
/// rewrite it replaces (WfsManager.WrapAsWfsFeatureCollection / ExtractBareFeatureById) from real
/// DDBQueryVector output (polygons, a later page, points, an empty result) and an edge-case file
/// (comments, empty/blank/nil leaves, nested and mixed content, CDATA, an empty featureMember, an
/// id without a fid). Outputs are compared element by element, ignoring the timeStamp and where
/// namespaces are declared.
/// </summary>
[TestFixture]
public class WfsFeatureCollectionWriterTests : TestBase
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "Wfs", name));

    /// <summary>Elements (prefix, namespace, name), attributes and text, without xmlns declarations.</summary>
    private static string Canonical(string xml, bool ignoreSeq = false)
    {
        var sb = new StringBuilder();
        void Write(XElement e, int depth)
        {
            sb.Append(' ', depth * 2).Append(e.GetPrefixOfNamespace(e.Name.Namespace)).Append(':')
                .Append(e.Name);
            foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != "timeStamp")
                         .OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
                sb.Append(' ').Append(a.Name).Append("=\"").Append(a.Value).Append('"');
            sb.Append('\n');
            foreach (var n in e.Nodes())
            {
                if (n is XElement child)
                    Write(child, depth + 1);
                else if (n is XText text && !(ignoreSeq && e.Name.LocalName == "ddbSeq"))
                    sb.Append(' ', depth * 2 + 2).Append('"').Append(text.Value).Append("\"\n");
            }
        }

        Write(XDocument.Parse(xml).Root!, 0);
        return sb.ToString();
    }

    [TestCase("polygons", "A010101.shp")]
    [TestCase("polygons-page", "A010101.shp")]
    [TestCase("points", "vector/test.geojson")]
    [TestCase("empty", "A010101.shp")]
    [TestCase("edge", "dir/my layer.gpkg")]
    public void ToFeatureCollection_MatchesTheDomRewrite(string fixture, string layerName)
    {
        var actual = WfsFeatureCollectionWriter.ToFeatureCollection(Fixture(fixture + ".input.gml"), layerName);

        actual.ShouldStartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?><wfs:FeatureCollection ");
        Canonical(actual).ShouldBe(Canonical(Fixture(fixture + ".expected.xml")));
    }

    [Test]
    public void ToFeatureCollection_ReportsNumberMatched()
    {
        var doc = XDocument.Parse(
            WfsFeatureCollectionWriter.ToFeatureCollection(Fixture("polygons.input.gml"), "A010101.shp", 25036));

        doc.Root!.Attribute("numberReturned")!.Value.ShouldBe("2");
        doc.Root!.Attribute("numberMatched")!.Value.ShouldBe("25036");
    }

    [Test]
    public void ToSingleFeature_MatchesTheDomExtraction()
    {
        // The fixture's second feature; ddbSeq is its position in the response, now 0
        var input = Fixture("polygons.input.gml");
        var second = input.IndexOf("<ogr:featureMember>", input.IndexOf("<ogr:featureMember>", StringComparison.Ordinal) + 1,
            StringComparison.Ordinal);
        var onlySecond = input[..input.IndexOf("<ogr:featureMember>", StringComparison.Ordinal)] + input[second..];

        var actual = WfsFeatureCollectionWriter.ToSingleFeature(onlySecond, "A010101.shp");

        actual.ShouldNotBeNull();
        Canonical(actual, ignoreSeq: true).ShouldBe(Canonical(Fixture("polygons.single.expected.xml"), ignoreSeq: true));
        WfsFeatureCollectionWriter.ToSingleFeature(Fixture("empty.input.gml"), "A010101.shp").ShouldBeNull();
    }

    [TestCase("A010101.shp.12", true, "A010101.shp", 12L)]
    [TestCase(" layer.7 ", true, "layer", 7L)]
    [TestCase("layer", false, "", 0L)]
    [TestCase("layer.x", false, "", 0L)]
    [TestCase(".5", false, "", 0L)]
    [TestCase("layer.-5", false, "", 0L)]
    public void FeatureIds_ParseWhatTheWriterProduces(string id, bool ok, string type, long fid)
    {
        WfsFeatureIds.TryParse(id, out var actualType, out var actualFid).ShouldBe(ok);
        actualType.ShouldBe(type);
        actualFid.ShouldBe(fid);
    }

    [Test]
    public void FeatureIds_RoundTrip()
    {
        var id = WfsFeatureIds.FromOgr("A010101.25036", OgcNames.ToNcName("dir/A010101.shp"));
        id.ShouldBe("dir_A010101.shp.25036");
        WfsFeatureIds.TryParse(id, out var type, out var fid).ShouldBeTrue();
        type.ShouldBe("dir_A010101.shp");
        fid.ShouldBe(25036);
    }

    /// <summary>OGR GML 3.2 shape for a polygon layer with 12 attributes.</summary>
    private static string BuildOgrGml(int features)
    {
        var sb = new StringBuilder(features * 3000);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n")
            .Append("<ogr:FeatureCollection xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("xmlns:ogr=\"http://ogr.maptools.org/\" xmlns:gml=\"http://www.opengis.net/gml/3.2\">\n")
            .Append("  <gml:boundedBy><gml:Envelope srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:lowerCorner>45 9</gml:lowerCorner><gml:upperCorner>46 10</gml:upperCorner></gml:Envelope></gml:boundedBy>\n");
        var rnd = new Random(1);
        for (var i = 0; i < features; i++)
        {
            var x = 9 + rnd.NextDouble();
            var y = 45 + rnd.NextDouble();
            sb.Append("  <ogr:featureMember>\n    <ogr:A010101 gml:id=\"A010101.").Append(i + 1).Append("\">\n")
                .Append("      <gml:boundedBy><gml:Envelope srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:lowerCorner>")
                .Append(y.ToString("F6", CultureInfo.InvariantCulture)).Append(' ').Append(x.ToString("F6", CultureInfo.InvariantCulture)).Append("</gml:lowerCorner><gml:upperCorner>")
                .Append((y + 0.001).ToString("F6", CultureInfo.InvariantCulture)).Append(' ').Append((x + 0.001).ToString("F6", CultureInfo.InvariantCulture))
                .Append("</gml:upperCorner></gml:Envelope></gml:boundedBy>\n")
                .Append("      <ogr:geom><gml:Polygon srsName=\"urn:ogc:def:crs:EPSG::4326\" gml:id=\"A010101.geom.")
                .Append(i + 1).Append("\"><gml:exterior><gml:LinearRing><gml:posList>");
            for (var k = 0; k < 24; k++)
                sb.Append((y + rnd.NextDouble() * 0.001).ToString("F7", CultureInfo.InvariantCulture)).Append(' ')
                    .Append((x + rnd.NextDouble() * 0.001).ToString("F7", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append("</gml:posList></gml:LinearRing></gml:exterior></gml:Polygon></ogr:geom>\n");
            for (var a = 0; a < 12; a++)
                sb.Append("      <ogr:ATTR_").Append(a).Append('>').Append(a == 3 ? "" : $"value {i}-{a}")
                    .Append("</ogr:ATTR_").Append(a).Append(">\n");
            sb.Append("    </ogr:A010101>\n  </ogr:featureMember>\n");
        }

        sb.Append("</ogr:FeatureCollection>\n");
        return sb.ToString();
    }

    [Test]
    public void ToFeatureCollection_10kFeatures_AllocatesAFewTimesTheInput()
    {
        const int features = 10_000;
        var gml = BuildOgrGml(features);
        var inputMb = Encoding.UTF8.GetByteCount(gml) / (1024.0 * 1024.0);

        WfsFeatureCollectionWriter.ToFeatureCollection(gml, "A010101.shp"); // warm-up
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var output = WfsFeatureCollectionWriter.ToFeatureCollection(gml, "A010101.shp");
        sw.Stop();
        var allocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - before) / (1024.0 * 1024.0);

        TestContext.Out.WriteLine(
            $"input {inputMb:F1} MB GML ({features} features) -> {Encoding.UTF8.GetByteCount(output) / (1024.0 * 1024.0):F1} MB; " +
            $"streaming rewrite: {sw.ElapsedMilliseconds} ms, {allocatedMb:F0} MB allocated ({allocatedMb / inputMb:F1}x input)");
        output.ShouldContain($"numberReturned=\"{features}\"");
        (allocatedMb / inputMb).ShouldBeLessThan(3.0, "baseline DOM rewrite: 13x");
    }
}
