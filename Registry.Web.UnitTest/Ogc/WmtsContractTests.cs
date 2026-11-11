using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;
using Registry.Web.Controllers.Ogc;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// The WMTS RESTful tile template (FR-SHR-01, FR-XC-31): the tile matrix set and the extensions of the URL that
/// GetCapabilities advertises in <c>ResourceURL</c>, the route, and the Share dialog of the Hub have one home,
/// <see cref="WmtsConformance"/>.
/// </summary>
[TestFixture]
public class WmtsContractTests
{
    private const string TemplateOfPng =
        "https://registry.test/orgs/org/ds/ds/wmts/1.0.0/ortho.tif/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png";

    private static XElement[] ResourceUrls(string capabilities) =>
        XDocument.Parse(capabilities).Descendants().Where(e => e.Name.LocalName == "ResourceURL").ToArray();

    [Test]
    public async Task GetCapabilities_GivesEachLayerAResourceUrlPerFormat_OfTheTileMatrixSetItLists()
    {
        var document = XDocument.Parse(await new OgcContractWorld().Wmts().GetCapabilitiesAsync("org", "ds"));
        var urls = document.Descendants().Where(e => e.Name.LocalName == "ResourceURL").ToArray();

        urls.Select(u => (string?)u.Attribute("format")).ShouldBe(["image/png", "image/jpeg"]);
        urls.ShouldAllBe(u => (string?)u.Attribute("resourceType") == "tile");
        urls[0].Attribute("template")!.Value.ShouldBe(TemplateOfPng);
        urls[1].Attribute("template")!.Value.ShouldBe(TemplateOfPng[..^3] + "jpg");

        // The set the template names is the one the capabilities define
        document.Descendants().Where(e => e.Name.LocalName == "TileMatrixSet").Select(e => e.Value)
            .ShouldContain("GoogleMapsCompatible");
        document.Descendants().Where(e => e.Name.LocalName == "Identifier").Select(e => e.Value)
            .ShouldContain("GoogleMapsCompatible");
    }

    /// <summary>The route values of a template filled in as a client does, and the extension of its file.</summary>
    private static (string Layer, string Style, string Tms, int Z, int Y, int X, string Ext) RouteOf(string template)
    {
        var url = template.Replace("{Style}", "default").Replace("{TileMatrixSet}", "GoogleMapsCompatible")
            .Replace("{TileMatrix}", "3").Replace("{TileRow}", "2").Replace("{TileCol}", "1");
        var parts = url[(url.IndexOf("/wmts/1.0.0/", StringComparison.Ordinal) + "/wmts/1.0.0/".Length)..].Split('/');
        var file = parts[5].Split('.');
        return (parts[0], parts[1], parts[2], int.Parse(parts[3]), int.Parse(parts[4]), int.Parse(file[0]), file[1]);
    }

    private static string PngTemplateOf(string capabilities) =>
        ResourceUrls(capabilities).Single(u => (string?)u.Attribute("format") == "image/png").Attribute("template")!.Value;

    private static async Task<string> TileCogOfAsync(OgcContractWorld world, string template)
    {
        var (layer, style, tms, z, y, x, ext) = RouteOf(template);
        var result = await new WmtsController(world.Wmts()).Restful("org", "ds", layer, style, tms, z, y, x, ext, default);
        result.ShouldBeOfType<FileContentResult>().ContentType.ShouldBe("image/png");
        var cogs = world.Ddb.Invocations.Where(i => i.Method.Name == "GenerateTile").Select(i => (string)i.Arguments[0]).ToList();
        return cogs.Single();
    }

    [Test]
    public async Task GetCapabilities_OfAFolder_WritesTheTemplateOfTheRealPathOfEachLayer()
    {
        // The RESTful route has no folder variant: the template is the dataset's, naming the layer by its path
        var world = new OgcContractWorld(["ortho.tif", "sub/ortho.tif", "sub/deep/map.tif"]);

        var xml = await world.Wmts().GetCapabilitiesAsync("org", "ds", folderPath: "sub");

        var templates = ResourceUrls(xml).Where(u => (string?)u.Attribute("format") == "image/png")
            .Select(u => u.Attribute("template")!.Value).ToList();
        templates.ShouldBe(
        [
            "https://registry.test/orgs/org/ds/ds/wmts/1.0.0/sub%2Fortho.tif/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png",
            "https://registry.test/orgs/org/ds/ds/wmts/1.0.0/sub%2Fdeep%2Fmap.tif/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png"
        ]);
        templates.ShouldAllBe(t => !t.Contains("/p/"));
    }

    [Test]
    public async Task RestfulGetTile_OfTheTemplateOfAFolderLayer_ServesThatLayer()
    {
        var world = new OgcContractWorld(["ortho.tif", "sub/ortho.tif"]);
        var xml = await world.Wmts().GetCapabilitiesAsync("org", "ds", folderPath: "sub");

        (await TileCogOfAsync(world, PngTemplateOf(xml))).ShouldBe("/data/hash-sub/ortho.tif-cog.tif");
    }

    [Test]
    public async Task RestfulGetTile_OfTheTemplateOfAFolderLayerWhoseSanitisedNameIsARootFile_ServesTheFolderLayer()
    {
        // "a/b.tif" and the root file "a_b.tif" are both "a_b.tif" once sanitised, and an exact name wins
        var world = new OgcContractWorld(["a_b.tif", "a/b.tif"]);
        var xml = await world.Wmts().GetCapabilitiesAsync("org", "ds", folderPath: "a");

        var template = PngTemplateOf(xml);

        template.ShouldContain("/wmts/1.0.0/a%2Fb.tif/{Style}");
        (await TileCogOfAsync(world, template)).ShouldBe("/data/hash-a/b.tif-cog.tif");
    }

    [Test]
    public async Task RestfulGetTile_OfTheTemplateOfTheRootFile_ServesTheRootFileNotTheFolderLayer()
    {
        var world = new OgcContractWorld(["a_b.tif", "a/b.tif"]);
        var xml = await world.Wmts().GetCapabilitiesAsync("org", "ds");

        var templates = ResourceUrls(xml).Where(u => (string?)u.Attribute("format") == "image/png")
            .Select(u => u.Attribute("template")!.Value).ToList();

        templates.ShouldContain(t => t.Contains("/wmts/1.0.0/a_b.tif/{Style}"));
        templates.ShouldContain(t => t.Contains("/wmts/1.0.0/a%2Fb.tif/{Style}"));
        (await TileCogOfAsync(world, templates.Single(t => t.Contains("/a_b.tif/")))).ShouldBe("/data/hash-a_b.tif-cog.tif");
    }

    [Test]
    public async Task RestfulGetTile_OfASanitisedLinkSharedBefore_StillServesTheLayer()
    {
        // The links of the Identifier ("a_b.tif" for "a/b.tif") that GetCapabilities gave before the real path
        var world = new OgcContractWorld(["a/b.tif"]);

        (await TileCogOfAsync(world, "https://registry.test/orgs/org/ds/ds/wmts/1.0.0/a_b.tif/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png"))
            .ShouldBe("/data/hash-a/b.tif-cog.tif");
    }

    [TestCase("GoogleMapsCompatible")]
    [TestCase("WebMercatorQuad")]
    [TestCase("googlemapscompatible")]
    public void TileMatrixSet_OfTheCapabilities_AndItsAlias_AreAccepted(string tms) =>
        Should.NotThrow(() => WmtsConformance.ValidateTileMatrixSet(tms));

    [TestCase("EPSG:4326")]
    [TestCase("WorldCRS84Quad")]
    public void TileMatrixSet_OfAnotherSet_IsRefused(string tms)
    {
        var ex = Should.Throw<OgcException>(() => WmtsConformance.ValidateTileMatrixSet(tms));

        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("tileMatrixSet");
    }

    [TestCase("pbf", "application/vnd.mapbox-vector-tile")]
    [TestCase("png", "image/png")]
    [TestCase("jpg", "image/jpeg")]
    [TestCase("JPEG", "image/jpeg")]
    public void Extension_MapsToItsFormat(string extension, string format) =>
        WmtsConformance.FormatForExtension(extension).ShouldBe(format);

    [Test]
    public void Extension_OfAnUnknownType_HasNoFormat() => WmtsConformance.FormatForExtension("gif").ShouldBeNull();

    [TestCase("image/png", "png")]
    [TestCase("image/jpeg", "jpg")]
    [TestCase("application/vnd.mapbox-vector-tile", "pbf")]
    public void Format_MapsToItsExtension(string format, string extension) =>
        WmtsConformance.ExtensionForFormat(format).ShouldBe(extension);

    [Test]
    public void EveryExtensionOfTheMap_IsOneThatTheRestfulRouteAccepts()
    {
        var route = typeof(WmtsController).GetMethod(nameof(WmtsController.Restful))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template!;
        var constraint = Regex.Match(route, @"\{ext:regex\((.*)\)\}").Groups[1].Value;

        constraint.ShouldBe(WmtsConformance.RestfulExtensionPattern);
        foreach (var (extension, _) in WmtsConformance.ExtensionFormats)
            Regex.IsMatch(extension, constraint).ShouldBeTrue(extension);
    }

    [TestCase("GoogleMapsCompatible")]
    [TestCase("WebMercatorQuad")]
    public async Task RestfulGetTile_WithEitherTileMatrixSet_IsAnImage(string tms)
    {
        var world = new OgcContractWorld();
        var controller = new WmtsController(world.Wmts());

        var result = await controller.Restful("org", "ds", "ortho.tif", "default", tms, 3, 2, 1, "png", default);

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe("image/png");
        file.FileContents.ShouldBe(OgcContractWorld.Png);
    }

    [Test]
    public async Task RestfulGetTile_OfTheTemplateTheCapabilitiesAdvertise_IsAnImage()
    {
        // The template of GetCapabilities, filled in as a client does, requested through the route of the controller
        var world = new OgcContractWorld();
        var capabilities = await world.Wmts().GetCapabilitiesAsync("org", "ds");
        var template = ResourceUrls(capabilities).First(u => (string?)u.Attribute("format") == "image/png")
            .Attribute("template")!.Value;
        var url = template.Replace("{Style}", "default").Replace("{TileMatrixSet}", "GoogleMapsCompatible")
            .Replace("{TileMatrix}", "3").Replace("{TileRow}", "2").Replace("{TileCol}", "1");

        // The route is the template of the capabilities: layer/style/matrix set/matrix/row/column.extension
        typeof(WmtsController).GetMethod(nameof(WmtsController.Restful))!.GetCustomAttribute<HttpGetAttribute>()!
            .Template!.ShouldStartWith("wmts/1.0.0/{layer}/{style}/{tms}/{z:int}/{y:int}/{x:int}.");
        var parts = url[(url.IndexOf("/wmts/1.0.0/", StringComparison.Ordinal) + "/wmts/1.0.0/".Length)..].Split('/');
        parts.Length.ShouldBe(6);
        var file = parts[5].Split('.');
        file.Length.ShouldBe(2);

        var result = await new WmtsController(world.Wmts()).Restful("org", "ds", parts[0], parts[1],
            parts[2], int.Parse(parts[3]), int.Parse(parts[4]), int.Parse(file[0]), file[1], default);

        result.ShouldBeOfType<FileContentResult>().ContentType.ShouldBe("image/png");
    }
}
