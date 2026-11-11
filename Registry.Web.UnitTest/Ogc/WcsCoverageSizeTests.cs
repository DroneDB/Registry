using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// WCS GetCoverage returns the coverage at its native size, or the size asked for, in WCS 1.0, 1.1 and 2.0
/// (FR-SHR-02, D-029): <see cref="WcsCoverageService"/> owns the native grid and the limit, and above the limit the
/// answer is an exception that states it, never a smaller image.
/// </summary>
[TestFixture]
public class WcsCoverageSizeTests
{
    private const string Layer = OgcContractWorld.Layer;

    // The coverage is [10, 40, 11, 41] over 12544 x 10240 pixels: a pixel is 1/12544 degree wide and 1/10240 high
    private const double Dx = 1.0 / OgcContractWorld.NativeWidth;
    private const double Dy = 1.0 / OgcContractWorld.NativeHeight;

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new StringValues(g.Select(p => p.Value).ToArray()), StringComparer.OrdinalIgnoreCase));

    private static string Lon(int pixels) => FormattableString.Invariant($"{10 + pixels * Dx:R}");

    private static string Lat(int pixels) => FormattableString.Invariant($"{41 - pixels * Dy:R}");

    // A request for the part of the coverage that is the top-left <width> x <height> pixels
    private static IQueryCollection Version20(int width, int height, params (string Key, string Value)[] more) =>
        Query([("COVERAGEID", Layer), ("FORMAT", "image/tiff"),
            ("SUBSET", $"Long(10,{Lon(width)})"), .. more, ("SUBSET", $"Lat({Lat(height)},41)")]);

    private static (int Width, int Height) RenderedSize(OgcContractWorld world)
    {
        var call = world.Wrapper.Invocations.Single(i => i.Method.Name == "RenderRasterRegion");
        return ((int)call.Arguments[3], (int)call.Arguments[4]);
    }

    // ---- GetCoverage without a subset: the grid DescribeCoverage gives (or the exception above the limit) ----

    [Test]
    public async Task Wcs20_WithoutASubset_AtTheNativeSizeWithinTheLimit_ReturnsTheGridOfDescribeCoverage()
    {
        var world = new OgcContractWorld();

        var described = XDocument.Parse(await world.Wcs(maxPixels: 16384).DescribeCoverageAsync("org", "ds", "2.0.1", Layer));
        var high = described.Descendants().Single(e => e.Name.LocalName == "high").Value;
        high.ShouldBe($"{OgcContractWorld.NativeWidth - 1} {OgcContractWorld.NativeHeight - 1}");

        await world.Wcs(maxPixels: 16384).GetCoverageAsync("org", "ds", "2.0.1",
            Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff")));

        RenderedSize(world).ShouldBe((OgcContractWorld.NativeWidth, OgcContractWorld.NativeHeight));
    }

    [TestCase("2.0.1")]
    [TestCase("1.1.1")]
    [TestCase("1.0.0")]
    public async Task WithoutASubset_AboveTheLimit_IsAnExceptionThatStatesTheLimit(string version)
    {
        var world = new OgcContractWorld();
        var query = version switch
        {
            "2.0.1" => Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff")),
            "1.1.1" => Query(("Identifier", Layer), ("FORMAT", "image/tiff"), ("BoundingBox", "10,40,11,41,urn:ogc:def:crs:EPSG::4326")),
            _ => Query(("COVERAGE", Layer), ("FORMAT", "GeoTIFF"), ("CRS", "EPSG:4326"), ("BBOX", "10,40,11,41"))
        };

        var ex = await Should.ThrowAsync<OgcException>(() => world.Wcs().GetCoverageAsync("org", "ds", version, query));

        ex.Code.ShouldBe("InvalidParameterValue");
        ex.HttpStatusCode.ShouldBe(400);
        ex.Message.ShouldContain($"{OgcContractWorld.NativeWidth}x{OgcContractWorld.NativeHeight}");
        ex.Message.ShouldContain("8192");
        world.Wrapper.Verify(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>()), Times.Never);
    }

    [TestCase("1.1.1")]
    [TestCase("1.0.0")]
    public async Task WithoutASubset_AtTheNativeSizeWithinTheLimit_ReturnsTheNativeGrid(string version)
    {
        var world = new OgcContractWorld();
        var query = version == "1.1.1"
            ? Query(("Identifier", Layer), ("FORMAT", "image/tiff"), ("BoundingBox", "10,40,11,41,urn:ogc:def:crs:EPSG::4326"))
            : Query(("COVERAGE", Layer), ("FORMAT", "GeoTIFF"), ("CRS", "EPSG:4326"), ("BBOX", "10,40,11,41"));

        await world.Wcs(maxPixels: 16384).GetCoverageAsync("org", "ds", version, query);

        RenderedSize(world).ShouldBe((OgcContractWorld.NativeWidth, OgcContractWorld.NativeHeight));
    }

    // ---- A subset of 256 x 256 pixels returns 256 x 256 ----

    [Test]
    public async Task Wcs20_ASubsetOf256x256Pixels_Returns256x256()
    {
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "2.0.1", Version20(256, 256));

        RenderedSize(world).ShouldBe((256, 256));
    }

    [Test]
    public async Task Wcs11_ASubsetOf256x256Pixels_Returns256x256()
    {
        var world = new OgcContractWorld();
        var bbox = FormattableString.Invariant($"10,{Lat(256)},{Lon(256)},41,urn:ogc:def:crs:EPSG::4326");

        await world.Wcs().GetCoverageAsync("org", "ds", "1.1.1",
            Query(("Identifier", Layer), ("FORMAT", "image/tiff"), ("BoundingBox", bbox)));

        RenderedSize(world).ShouldBe((256, 256));
    }

    [Test]
    public async Task Wcs10_ASubsetOf256x256Pixels_Returns256x256()
    {
        var world = new OgcContractWorld();
        var bbox = FormattableString.Invariant($"10,{Lat(256)},{Lon(256)},41");

        await world.Wcs().GetCoverageAsync("org", "ds", "1.0.0",
            Query(("COVERAGE", Layer), ("FORMAT", "GeoTIFF"), ("CRS", "EPSG:4326"), ("BBOX", bbox)));

        RenderedSize(world).ShouldBe((256, 256));
    }

    // ---- The size asked for is honoured, and the limit is an exception ----

    [Test]
    public async Task Wcs20_ScaleSize_IsTheOutputSize()
    {
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "2.0.1",
            Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff"), ("SCALESIZE", "Long(4000),Lat(3000)")));

        RenderedSize(world).ShouldBe((4000, 3000));
    }

    [Test]
    public async Task Wcs20_ScaleSize_OfTheSizeTheSubsetHas_Returns256x256()
    {
        // What gdal_translate -srcwin 0 0 256 256 sends: the subset and the size of it
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "2.0.1", Version20(256, 256, ("SCALESIZE", "Long(256),Lat(256)")));

        RenderedSize(world).ShouldBe((256, 256));
    }

    [Test]
    public async Task Wcs20_ScaleFactor_ScalesTheNativeSizeOfTheSubset()
    {
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "2.0.1", Version20(1000, 800, ("SCALEFACTOR", "0.5")));

        RenderedSize(world).ShouldBe((500, 400));
    }

    [Test]
    public async Task Wcs20_ScaleAxes_ScalesEachAxis()
    {
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "2.0.1",
            Version20(1000, 800, ("SCALEAXES", "Long(0.5),Lat(0.25)")));

        RenderedSize(world).ShouldBe((500, 200));
    }

    [Test]
    public async Task Wcs20_AskedSizeAbove8192_IsAnExceptionNotASmallerImage()
    {
        var world = new OgcContractWorld();

        var ex = await Should.ThrowAsync<OgcException>(() => world.Wcs().GetCoverageAsync("org", "ds", "2.0.1",
            Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff"), ("SCALESIZE", "Long(9000),Lat(100)"))));

        ex.Message.ShouldContain("9000x100");
        ex.Message.ShouldContain("limit of 8192");
    }

    [Test]
    public async Task Wcs10_WidthAbove4096_IsHonoured_AndAbove8192IsAnException()
    {
        var world = new OgcContractWorld();
        IQueryCollection Asked(int width) => Query(("COVERAGE", Layer), ("FORMAT", "GeoTIFF"), ("CRS", "EPSG:4326"),
            ("BBOX", "10,40,11,41"), ("WIDTH", width.ToString()), ("HEIGHT", "100"));

        await world.Wcs().GetCoverageAsync("org", "ds", "1.0.0", Asked(8000));
        RenderedSize(world).ShouldBe((8000, 100));

        var ex = await Should.ThrowAsync<OgcException>(() => world.Wcs().GetCoverageAsync("org", "ds", "1.0.0", Asked(8193)));
        ex.Message.ShouldContain("8193x100");
    }

    [Test]
    public async Task Wcs11_GridOffsets_GiveTheSize()
    {
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "1.1.1",
            Query(("Identifier", Layer), ("FORMAT", "image/tiff"),
                ("BoundingBox", "10,40,11,41,urn:ogc:def:crs:EPSG::4326"), ("GridOffsets", "0.0002,-0.0002")));

        RenderedSize(world).ShouldBe((5000, 5000));
    }

    [Test]
    public async Task ASmallSize_IsNotRaisedToAMinimum()
    {
        var world = new OgcContractWorld();

        await world.Wcs().GetCoverageAsync("org", "ds", "2.0.1",
            Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff"), ("SCALESIZE", "Long(10),Lat(8)")));

        RenderedSize(world).ShouldBe((10, 8));
    }

    [Test]
    public async Task TheLimit_IsTheConfiguredOne()
    {
        var world = new OgcContractWorld();

        var ex = await Should.ThrowAsync<OgcException>(() => world.Wcs(maxPixels: 1000).GetCoverageAsync("org", "ds",
            "2.0.1", Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff"), ("SCALESIZE", "Long(1001),Lat(10)"))));
        ex.Message.ShouldContain("limit of 1000");

        await world.Wcs(maxPixels: 1000).GetCoverageAsync("org", "ds", "2.0.1",
            Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff"), ("SCALESIZE", "Long(1000),Lat(10)")));
        RenderedSize(world).ShouldBe((1000, 10));
    }

    [Test]
    public void TheLimit_DefaultsTo8192()
    {
        DownloadOptions.DefaultMaxWcsCoverageSizePixels.ShouldBe(8192);
        new OgcContractWorld().WcsService().MaxCoverageSizePixels.ShouldBe(8192);
        new OgcContractWorld().WcsService(maxPixels: 0).MaxCoverageSizePixels.ShouldBe(8192);
    }

    // ---- DescribeCoverage of the three versions says what GetCoverage serves ----

    [Test]
    public async Task DescribeCoverage_OfEveryVersion_HasTheSameNativeGrid()
    {
        var world = new OgcContractWorld();
        var wcs = world.Wcs();
        var grid = WcsCoverageService.NativeGrid(
            new WcsRasterInfo(OgcContractWorld.NativeWidth, OgcContractWorld.NativeHeight, 3, []), [10, 40, 11, 41]);
        grid.Width.ShouldBe(OgcContractWorld.NativeWidth);
        grid.Dx.ShouldBe(Dx, 1e-15);

        var v20 = XDocument.Parse(await wcs.DescribeCoverageAsync("org", "ds", "2.0.1", Layer));
        var v11 = XDocument.Parse(await wcs.DescribeCoverageAsync("org", "ds", "1.1.1", Layer));
        var v10 = XDocument.Parse(await wcs.DescribeCoverageAsync("org", "ds", "1.0.0", Layer));

        var expected = $"{OgcContractWorld.NativeWidth - 1} {OgcContractWorld.NativeHeight - 1}";
        v20.Descendants().Single(e => e.Name.LocalName == "high").Value.ShouldBe(expected);
        v10.Descendants().Single(e => e.Name.LocalName == "high").Value.ShouldBe(expected);

        double Pixel(XDocument d, int i) => double.Parse(
            d.Descendants().Where(e => e.Name.LocalName == "GridOffsets").Single().Value.Split(' ')[i],
            System.Globalization.CultureInfo.InvariantCulture);
        Pixel(v11, 0).ShouldBe(Dx, 1e-15);
        Math.Abs(Pixel(v11, 1)).ShouldBe(Dy, 1e-15);
        double.Parse(v20.Descendants().First(e => e.Name.LocalName == "offsetVector").Value.Split(' ')[0],
            System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Dx, 1e-15);
    }

    [Test]
    public void NativeGrid_OfARasterWhoseSizeCouldNotBeRead_IsASquareOf1024()
    {
        var grid = WcsCoverageService.NativeGrid(WcsRasterInfo.Empty, [0, 0, 2, 1]);

        (grid.Width, grid.Height).ShouldBe((1024, 1024));
        grid.Dx.ShouldBe(2.0 / 1024);
        grid.Dy.ShouldBe(1.0 / 1024);
    }

    [Test]
    public void SizeOf_IsTheSpanOverThePixelSize_AtLeastOnePixel()
    {
        var grid = new WcsGrid(1000, 500, 0.001, 0.002);

        grid.SizeOf([0, 0, 0.256, 0.5]).ShouldBe((256, 250));
        grid.SizeOf([0, 0, 0.0001, 0.0001]).ShouldBe((1, 1));
    }

    // ---- The Scaling extension parser ----

    [TestCase(null, null, null, null, null)]
    [TestCase("2", null, null, 2000, 2000)]
    [TestCase("0.1", null, null, 100, 100)]
    [TestCase(null, "x(0.5),y(2)", null, 500, 2000)]
    [TestCase(null, "Lat(2)", null, 1000, 2000)]
    [TestCase(null, null, "i(10),j(20)", 10, 20)]
    [TestCase(null, null, "Long(300)", 300, 1000)]
    public void ScalingParser_GivesTheSizeOfTheOutput(string? factor, string? axes, string? size, int? width, int? height)
    {
        var parsed = WcsScalingParser.Parse(factor, axes, size, (1000, 1000));

        if (width is null) parsed.ShouldBeNull();
        else parsed.ShouldBe((width.Value, height!.Value));
    }

    // OGC 12-039 Table 7: InvalidScaleFactor (a factor that is not a positive number) and ScaleAxisUndefined (an
    // axis the coverage does not have), both HTTP 404; the standard has no code for the other errors
    [TestCase("0", null, null, "InvalidScaleFactor", 404)]
    [TestCase("-1", null, null, "InvalidScaleFactor", 404)]
    [TestCase("abc", null, null, "InvalidScaleFactor", 404)]
    [TestCase(null, "x(0)", null, "InvalidScaleFactor", 404)]
    [TestCase(null, "x(abc)", null, "InvalidScaleFactor", 404)]
    [TestCase(null, null, "z(10)", "ScaleAxisUndefined", 404)]
    [TestCase(null, "z(2)", null, "ScaleAxisUndefined", 404)]
    [TestCase(null, null, "x(0)", "InvalidParameterValue", 400)]
    [TestCase(null, null, "x(1.5)", "InvalidParameterValue", 400)]
    [TestCase(null, null, "x", "InvalidParameterValue", 400)]
    [TestCase(null, null, "x(1),x(2)", "InvalidParameterValue", 400)]
    [TestCase("2", "x(2)", null, "InvalidParameterValue", 400)]
    public void ScalingParser_RefusesWhatItCannotRead(string? factor, string? axes, string? size, string code, int http)
    {
        var ex = Should.Throw<OgcException>(() => WcsScalingParser.Parse(factor, axes, size, (1000, 1000)));

        ex.Code.ShouldBe(code);
        ex.HttpStatusCode.ShouldBe(http);
    }

    [Test]
    public async Task Wcs20_GetCapabilities_AdvertisesTheScalingConformanceClass()
    {
        // OGC 12-039 requirement 1 (extension-identifier): the URI is a Profile of the ServiceIdentification
        var world = new OgcContractWorld();

        var xml = await world.Wcs().GetCapabilitiesAsync("org", "ds", "2.0.1");

        var identification = System.Xml.Linq.XDocument.Parse(xml).Descendants()
            .Single(e => e.Name.LocalName == "ServiceIdentification");
        identification.Elements().Where(e => e.Name.LocalName == "Profile").Select(e => e.Value).ShouldBe(
        [
            "http://www.opengis.net/spec/WCS/2.0/conf/core",
            "http://www.opengis.net/spec/WCS_protocol-binding_get-kvp/1.0/conf/get-kvp",
            "http://www.opengis.net/spec/WCS_service-extension_scaling/1.0/conf/scaling"
        ]);
    }

    [Test]
    public async Task Wcs20_GetCoverage_WithAScaleFactorThatIsNotPositive_IsInvalidScaleFactor()
    {
        var world = new OgcContractWorld();

        var ex = await Should.ThrowAsync<OgcException>(() => world.Wcs().GetCoverageAsync("org", "ds", "2.0.1",
            Query(("COVERAGEID", Layer), ("FORMAT", "image/tiff"), ("SCALEFACTOR", "0"))));

        ex.Code.ShouldBe("InvalidScaleFactor");
        ex.HttpStatusCode.ShouldBe(404);
    }
}
