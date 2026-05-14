using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers;

/// <summary>WMTS 1.0.0 controller — KVP + RESTful tile retrieval.</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WmtsController : ControllerBaseEx
{
    private readonly IWmtsManager _mgr;
    private readonly ILogger<WmtsController> _logger;

    public WmtsController(IWmtsManager mgr, ILogger<WmtsController> logger)
    {
        _mgr = mgr; _logger = logger;
    }

    [HttpGet("wmts")]
    public async Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => await Dispatch(orgSlug, dsSlug, folderPath: null);

    [HttpGet("wmts/p/{*folder}")]
    public async Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => await Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "GETTILE":
                var layer = OgcRequestParser.GetRequired(q, "LAYER");
                var tms = OgcRequestParser.Get(q, "TILEMATRIXSET") ?? "GoogleMapsCompatible";
                var z = OgcRequestParser.GetInt(q, "TILEMATRIX", 0, 0, 24);
                var x = OgcRequestParser.GetInt(q, "TILECOL", 0, 0);
                var y = OgcRequestParser.GetInt(q, "TILEROW", 0, 0);
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, tms, z, x, y, format);
                return File(bytes, format);
            default:
                throw new OgcException("OperationNotSupported",
                    $"WMTS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }

    [HttpGet("wmts/1.0.0/{layer}/{style}/{tms}/{z:int}/{y:int}/{x:int}.{ext}")]
    public async Task<IActionResult> Restful([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string layer, [FromRoute] string style, [FromRoute] string tms,
        [FromRoute] int z, [FromRoute] int y, [FromRoute] int x, [FromRoute] string ext)
    {
        var format = ext.ToLowerInvariant() switch
        {
            "pbf" => "application/vnd.mapbox-vector-tile",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
        // Route values may arrive percent-encoded when the layer identifier contains '/' (e.g. "folder%2Ffile.shp").
        layer = Uri.UnescapeDataString(layer);
        var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, tms, z, x, y, format);
        return File(bytes, format);
    }
}

/// <summary>WMS 1.3.0 controller (with best-effort 1.1.1 negotiation).</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WmsController : ControllerBaseEx
{
    private readonly IWmsManager _mgr;
    public WmsController(IWmsManager mgr) { _mgr = mgr; }

    [HttpGet("wms")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wms/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        var version = OgcRequestParser.NegotiateWmsVersion(OgcRequestParser.Get(q, "VERSION"));
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, version, folderPath),
                    "text/xml; charset=utf-8");

            case "GETMAP":
            {
                var layers = OgcRequestParser.GetList(q, "LAYERS") ?? Array.Empty<string>();
                var styles = OgcRequestParser.GetList(q, "STYLES") ?? Array.Empty<string>();
                var crs = OgcRequestParser.Get(q, version == "1.3.0" ? "CRS" : "SRS") ?? "EPSG:4326";
                var bboxStr = OgcRequestParser.GetRequired(q, "BBOX");
                var (bbox, resolvedCrs) = OgcRequestParser.ParseBbox(bboxStr, crs, version);
                var width = OgcRequestParser.GetInt(q, "WIDTH", 256, 1, 4096);
                var height = OgcRequestParser.GetInt(q, "HEIGHT", 256, 1, 4096);
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                var bg = OgcRequestParser.Get(q, "BGCOLOR");
                var trans = string.Equals(OgcRequestParser.Get(q, "TRANSPARENT"), "TRUE",
                    StringComparison.OrdinalIgnoreCase);
                var bytes = await _mgr.GetMapAsync(orgSlug, dsSlug, layers, styles, bbox, resolvedCrs,
                    width, height, format, bg, trans);
                return File(bytes, format);
            }

            case "GETFEATUREINFO":
            {
                var queryLayers = OgcRequestParser.GetList(q, "QUERY_LAYERS")
                                  ?? OgcRequestParser.GetList(q, "LAYERS")
                                  ?? Array.Empty<string>();
                if (queryLayers.Length == 0)
                    throw new OgcException("MissingParameterValue", "QUERY_LAYERS required", 400, "QUERY_LAYERS");
                var crs = OgcRequestParser.Get(q, version == "1.3.0" ? "CRS" : "SRS") ?? "EPSG:4326";
                var bboxStr = OgcRequestParser.GetRequired(q, "BBOX");
                var (bbox, resolvedCrs) = OgcRequestParser.ParseBbox(bboxStr, crs, version);
                var width = OgcRequestParser.GetInt(q, "WIDTH", 256, 1, 4096);
                var height = OgcRequestParser.GetInt(q, "HEIGHT", 256, 1, 4096);
                var i = OgcRequestParser.GetInt(q, version == "1.3.0" ? "I" : "X", 0, 0, width - 1);
                var j = OgcRequestParser.GetInt(q, version == "1.3.0" ? "J" : "Y", 0, 0, height - 1);
                var info = OgcRequestParser.Get(q, "INFO_FORMAT") ?? "application/json";
                var body = await _mgr.GetFeatureInfoAsync(orgSlug, dsSlug, queryLayers[0], bbox, resolvedCrs,
                    width, height, i, j, info);
                return Content(body, info);
            }

            default:
                throw new OgcException("OperationNotSupported",
                    $"WMS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}

/// <summary>WFS 2.0.0 controller.</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WfsController : ControllerBaseEx
{
    private readonly IWfsManager _mgr;
    public WfsController(IWfsManager mgr) { _mgr = mgr; }

    [HttpGet("wfs")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wfs/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "DESCRIBEFEATURETYPE":
            {
                var typeNames = OgcRequestParser.GetList(q, "TYPENAMES")
                                ?? OgcRequestParser.GetList(q, "TYPENAME")
                                ?? Array.Empty<string>();
                return Content(await _mgr.DescribeFeatureTypeAsync(orgSlug, dsSlug, typeNames),
                    "text/xml; charset=utf-8");
            }
            case "GETFEATURE":
            {
                var typeName = (OgcRequestParser.Get(q, "TYPENAMES")
                                ?? OgcRequestParser.Get(q, "TYPENAME"))
                               ?? throw new OgcException("MissingParameterValue",
                                   "typeNames required", 400, "typeNames");
                var bbox = OgcRequestParser.Get(q, "BBOX");
                double[]? bboxArr = null;
                string? bboxCrs = null;
                if (!string.IsNullOrWhiteSpace(bbox))
                {
                    var parsed = OgcRequestParser.ParseBbox(bbox, OgcRequestParser.Get(q, "SRSNAME"), "2.0.0");
                    bboxArr = parsed.Bbox;
                    bboxCrs = parsed.Crs;
                }
                var count = OgcRequestParser.GetInt(q, "COUNT", 1000, 1, 10000);
                count = Math.Max(count, OgcRequestParser.GetInt(q, "MAXFEATURES", count, 1, 10000));
                var startIndex = OgcRequestParser.GetInt(q, "STARTINDEX", 0, 0);
                var format = OgcRequestParser.Get(q, "OUTPUTFORMAT") ?? "application/json";
                var body = await _mgr.GetFeatureAsync(orgSlug, dsSlug, typeName, bboxArr, bboxCrs,
                    count, startIndex, format);
                return Content(body, format);
            }
            default:
                throw new OgcException("OperationNotSupported",
                    $"WFS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}

/// <summary>WCS 2.0 controller.</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WcsController : ControllerBaseEx
{
    private readonly IWcsManager _mgr;
    public WcsController(IWcsManager mgr) { _mgr = mgr; }

    [HttpGet("wcs")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wcs/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "DESCRIBECOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                return Content(await _mgr.DescribeCoverageAsync(orgSlug, dsSlug, cid),
                    "text/xml; charset=utf-8");
            }
            case "GETCOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                double[]? subset = null;
                var subsetParams = q
                    .Where(kv => string.Equals(kv.Key, "SUBSET", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                if (subsetParams.Length >= 2)
                {
                    // Best-effort minimal SUBSET parser: lat(minLat,maxLat),long(minLon,maxLon)
                    double? minLat = null, maxLat = null, minLon = null, maxLon = null;
                    foreach (var part in subsetParams)
                    {
                        var open = part.IndexOf('(');
                        var close = part.IndexOf(')');
                        if (open < 0 || close < 0) continue;
                        var axis = part[..open].Trim().ToLowerInvariant();
                        var range = part.Substring(open + 1, close - open - 1).Split(',');
                        if (range.Length != 2) continue;
                        if (!double.TryParse(range[0], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var lo) ||
                            !double.TryParse(range[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var hi)) continue;
                        if (axis.StartsWith("lat") || axis == "y") { minLat = lo; maxLat = hi; }
                        else if (axis.StartsWith("lon") || axis.StartsWith("long") || axis == "x")
                            { minLon = lo; maxLon = hi; }
                    }
                    if (minLon.HasValue && minLat.HasValue && maxLon.HasValue && maxLat.HasValue)
                        subset = new[] { minLon.Value, minLat.Value, maxLon.Value, maxLat.Value };
                }
                var bytes = await _mgr.GetCoverageAsync(orgSlug, dsSlug, cid, subset, format);
                return File(bytes, format);
            }
            default:
                throw new OgcException("OperationNotSupported",
                    $"WCS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}

/// <summary>OGC API – Features + Tiles controller (JSON REST).</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug + "/ogcapi")]
public class OgcApiController : ControllerBaseEx
{
    private readonly IOgcApiFeaturesManager _features;
    private readonly IOgcApiTilesManager _tiles;

    public OgcApiController(IOgcApiFeaturesManager features, IOgcApiTilesManager tiles)
    {
        _features = features; _tiles = tiles;
    }

    private string BaseUrl([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => $"{Request.Scheme}://{Request.Host}/orgs/{orgSlug}/ds/{dsSlug}/ogcapi";

    [HttpGet("")]
    public async Task<IActionResult> Landing([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Ok(await _features.GetLandingAsync(orgSlug, dsSlug, BaseUrl(orgSlug, dsSlug)));

    [HttpGet("conformance")]
    public async Task<IActionResult> Conformance()
        => Ok(await _features.GetConformanceAsync());

    [HttpGet("collections")]
    public async Task<IActionResult> Collections([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Ok(await _features.GetCollectionsAsync(orgSlug, dsSlug, BaseUrl(orgSlug, dsSlug)));

    [HttpGet("collections/{collectionId}")]
    public async Task<IActionResult> Collection([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId)
    {
        var col = await _features.GetCollectionAsync(orgSlug, dsSlug, collectionId, BaseUrl(orgSlug, dsSlug));
        return col == null ? NotFound() : Ok(col);
    }

    [HttpGet("collections/{collectionId}/items")]
    public async Task<IActionResult> Items([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId,
        [FromQuery] string? bbox = null,
        [FromQuery] int limit = 10,
        [FromQuery] int offset = 0)
    {
        double[]? bboxArr = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parsed = OgcRequestParser.ParseBbox(bbox, "CRS:84", null);
            bboxArr = parsed.Bbox;
        }
        var json = await _features.GetItemsAsync(orgSlug, dsSlug, collectionId, bboxArr, limit, offset);
        return Content(json, "application/geo+json");
    }

    [HttpGet("collections/{collectionId}/items/{featureId}")]
    public async Task<IActionResult> Item([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, [FromRoute] string featureId)
    {
        var json = await _features.GetItemAsync(orgSlug, dsSlug, collectionId, featureId);
        return Content(json, "application/geo+json");
    }

    [HttpGet("collections/{collectionId}/tiles")]
    public async Task<IActionResult> TileSets([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId)
        => Ok(await _tiles.GetTileSetsAsync(orgSlug, dsSlug, collectionId, BaseUrl(orgSlug, dsSlug)));

    [HttpGet("collections/{collectionId}/tiles/{tileMatrixSet}/{z:int}/{y:int}/{x:int}")]
    public async Task<IActionResult> Tile([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, [FromRoute] string tileMatrixSet,
        [FromRoute] int z, [FromRoute] int y, [FromRoute] int x)
    {
        var bytes = await _tiles.GetTileAsync(orgSlug, dsSlug, collectionId, tileMatrixSet, z, x, y);
        if (bytes == null) return NotFound();
        return File(bytes, "application/octet-stream");
    }
}
