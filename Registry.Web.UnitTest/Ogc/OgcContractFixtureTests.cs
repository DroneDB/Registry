using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wfs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// The Hub's OGC share templates and service table (webapp/js/libs/ogcShareUrls.js, FR-SHR-01, FR-XC-31, AC-015) are
/// those of this Registry. <c>Fixtures/ogc-contract.json</c> is a copy of the Hub's
/// <c>webapp/js/libs/__vectors__/ogc-contract.json</c> (the Hub's vitest reads that one, these tests read this one):
/// the two files must change together with the code of both.
/// </summary>
[TestFixture]
public class OgcContractFixtureTests
{
    private static readonly string[] Rasters = ["ortho.tif", "odm_orthophoto/odm_orthophoto.tif", "my map (1).tif"];

    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(NUnit.Framework.TestContext.CurrentContext.TestDirectory, "Ogc", "Fixtures", "ogc-contract.json")))!.AsObject();

    private static string Local(XElement e, string child) =>
        e.Elements().Where(c => c.Name.LocalName == child).Select(c => c.Value).SingleOrDefault() ?? "";

    /// <summary>What a Layer of the capabilities says of how to request it, for the comparison with the fixture.</summary>
    private static List<string> LayersOf(string capabilities) => XDocument.Parse(capabilities).Descendants()
        .Where(e => e.Name.LocalName == "Layer")
        .Select(layer => string.Join("\n", new[]
        {
            "title=" + Local(layer, "Title"),
            "identifier=" + Local(layer, "Identifier"),
            "style=" + layer.Elements().Single(e => e.Name.LocalName == "Style").Elements().Single().Value,
            "formats=" + string.Join(",", layer.Elements().Where(e => e.Name.LocalName == "Format").Select(e => e.Value)),
            "link=" + layer.Elements().Single(e => e.Name.LocalName == "TileMatrixSetLink").Value
        }.Concat(layer.Elements().Where(e => e.Name.LocalName == "ResourceURL")
            .Select(u => $"resource={(string?)u.Attribute("format")} {(string?)u.Attribute("resourceType")} {(string?)u.Attribute("template")}"))))
        .ToList();

    private static string MatrixSetOf(string capabilities) => XDocument.Parse(capabilities).Root!.Elements()
        .Single(e => e.Name.LocalName == "Contents").Elements().Single(e => e.Name.LocalName == "TileMatrixSet")
        .Elements().Single(e => e.Name.LocalName == "Identifier").Value;

    [Test]
    public async Task GetCapabilities_GivesTheLayersAndTemplatesOfTheFixture_NestedAndNonNcNameLayersIncluded()
    {
        var fixtureCapabilities = (string)Fixture()["wmtsCapabilities"]!;
        var capabilities = await new OgcContractWorld(Rasters).Wmts().GetCapabilitiesAsync("org", "ds");

        var layers = LayersOf(capabilities);

        layers.Count.ShouldBe(3);
        layers.ShouldBe(LayersOf(fixtureCapabilities));
        MatrixSetOf(capabilities).ShouldBe(MatrixSetOf(fixtureCapabilities));
        // The nested raster is named by its escaped path (AC-015), the layer that is not an NCName likewise
        layers[1].ShouldContain("/wmts/1.0.0/odm_orthophoto%2Fodm_orthophoto.tif/{Style}/");
        layers[2].ShouldContain("/wmts/1.0.0/my%20map%20%281%29.tif/{Style}/");
    }

    [Test]
    public void RestfulExtensions_AreTheOnesOfTheRoute()
    {
        var fixture = Fixture()["restfulExtensions"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);

        fixture.ShouldBe(WmtsConformance.ExtensionFormats.ToDictionary(p => p.Key, p => p.Value), ignoreOrder: true);
    }

    /// <summary>The services of the fixture's table that serve <paramref name="world"/>'s one layer, by what each does with it.</summary>
    private static async Task<string[]> ServicesServingAsync(OgcContractWorld world, string path, bool raster)
    {
        var served = new List<string>();
        var d = world.Dependencies;

        var indexes = new Mock<IRasterIndexCatalog>();
        indexes.Setup(i => i.GetAsync(It.IsAny<string>(), It.IsAny<RasterSource>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync([]);
        var wms = new WmsManager(d, NullLogger<WmsManager>.Instance, Mock.Of<Registry.Ports.ICacheManager>(), indexes.Object);
        if (XDocument.Parse(await wms.GetCapabilitiesAsync("org", "ds", "1.3.0")).Descendants()
            .Any(e => e.Name.LocalName == "Name" && e.Value == path)) served.Add("wms");

        // The RESTful route of WMTS serves a raster as png and a vector layer as pbf
        var tile = await world.Wmts().GetTileAsync("org", "ds", path, "default", "GoogleMapsCompatible", 3, 2, 1,
            raster ? "image/png" : WmtsConformance.VectorFormat);
        if (tile.SequenceEqual(raster ? OgcContractWorld.Png : OgcContractWorld.Mvt)) served.Add("wmts");

        var wfs = new WfsManager(new WfsCapabilities(d), null!, null!);
        if (XDocument.Parse(await wfs.GetCapabilitiesAsync("org", "ds")).Descendants()
            .Any(e => e.Name.LocalName == "FeatureType")) served.Add("wfs");

        if ((await world.WcsService().GetCoveragesAsync("org", "ds", null)).Layers.Any(l => l.EntryPath == path))
            served.Add("wcs");

        try
        {
            await new OgcApiFeaturesManager(d, NullLogger<OgcApiFeaturesManager>.Instance)
                .GetItemsAsync("org", "ds", Uri.EscapeDataString(path), null, 10, 0);
            served.Add("ogcApiFeatures");
        }
        catch (OgcException)
        {
            // Refused: the service does not serve this entry type
        }

        var sets = await new OgcApiTilesManager(d, null!).GetTileSetsAsync("org", "ds", Uri.EscapeDataString(path),
            "https://registry.test/orgs/org/ds/ds/ogcapi");
        if (sets.Tilesets.Count > 0) served.Add("ogcApiTiles");

        return [.. served.Order(StringComparer.Ordinal)];
    }

    private static string[] FixtureServices(string entryType) =>
        [.. Fixture()["servicesByEntryType"]![entryType]!.AsArray().Select(n => (string)n!).Order(StringComparer.Ordinal)];

    [Test]
    public async Task ServicesOfARaster_AreTheOnesOfTheFixture() =>
        (await ServicesServingAsync(new OgcContractWorld(["ortho.tif"]), "ortho.tif", raster: true))
            .ShouldBe(FixtureServices("georaster"));

    [Test]
    public async Task ServicesOfAVector_AreTheOnesOfTheFixture() =>
        (await ServicesServingAsync(new OgcContractWorld(null, ["roads.fgb"]), "roads.fgb", raster: false))
            .ShouldBe(FixtureServices("vector"));
}
