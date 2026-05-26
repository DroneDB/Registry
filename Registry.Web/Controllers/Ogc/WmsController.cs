using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers;

/// <summary>WMS 1.3.0 controller (with best-effort 1.1.1 negotiation).</summary>
[ApiController]
[ServiceFilter(typeof(BasicAuthFilter))]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
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
        var rawVersion = OgcRequestParser.Get(q, "VERSION");
        var version = OgcRequestParser.NegotiateWmsVersion(rawVersion);
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, version, folderPath),
                    "text/xml; charset=utf-8");

            case "GETMAP":
            {
                if (string.IsNullOrWhiteSpace(rawVersion))
                    throw new OgcException("MissingParameterValue", "VERSION is required for GetMap", 400, "VERSION");
                var layers = OgcRequestParser.GetList(q, "LAYERS") ?? Array.Empty<string>();
                var styles = OgcRequestParser.GetList(q, "STYLES") ?? Array.Empty<string>();
                var crs = OgcRequestParser.Get(q, version == "1.3.0" ? "CRS" : "SRS") ?? "EPSG:4326";
                var width = OgcRequestParser.GetInt(q, "WIDTH", 256, 1, 4096);
                var height = OgcRequestParser.GetInt(q, "HEIGHT", 256, 1, 4096);
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";

                // Validate cheap parameters BEFORE BBOX parsing so the most common
                // CITE failure modes (missing CRS, bad FORMAT, oversized WIDTH/HEIGHT)
                // surface with their specific OGC exception code instead of being
                // masked by an "InvalidParameterValue: BBOX" from ParseBbox.
                WmsValidator.ValidateLayers(layers);
                WmsValidator.ValidateCrs(crs);
                WmsValidator.ValidateMapFormat(format);
                WmsValidator.ValidateDimensions(width, height);

                var bboxStr = OgcRequestParser.GetRequired(q, "BBOX");
                var (bbox, resolvedCrs) = OgcRequestParser.ParseBbox(bboxStr, crs, version);
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
                var width = OgcRequestParser.GetInt(q, "WIDTH", 256, 1, 4096);
                var height = OgcRequestParser.GetInt(q, "HEIGHT", 256, 1, 4096);
                WmsValidator.ValidateCrs(crs);
                WmsValidator.ValidateDimensions(width, height);

                var bboxStr = OgcRequestParser.GetRequired(q, "BBOX");
                var (bbox, resolvedCrs) = OgcRequestParser.ParseBbox(bboxStr, crs, version);
                var iKey = version == "1.3.0" ? "I" : "X";
                var jKey = version == "1.3.0" ? "J" : "Y";
                var iRaw = OgcRequestParser.GetRequired(q, iKey);
                var jRaw = OgcRequestParser.GetRequired(q, jKey);
                if (!int.TryParse(iRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)
                    || i < 0 || i >= width)
                    throw new OgcException("InvalidPoint", $"{iKey} must be in [0,{width - 1}]", 400, iKey);
                if (!int.TryParse(jRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var j)
                    || j < 0 || j >= height)
                    throw new OgcException("InvalidPoint", $"{jKey} must be in [0,{height - 1}]", 400, jKey);
                var info = OgcRequestParser.Get(q, "INFO_FORMAT") ?? "application/json";
                WmsValidator.ValidateInfoFormat(info);
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
