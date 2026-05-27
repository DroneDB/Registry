#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers.Ogc;

/// <summary>WMTS 1.0.0 controller — KVP + RESTful tile retrieval.</summary>
[ApiController]
[ServiceFilter(typeof(BasicAuthFilter))]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
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

    private static readonly string[] SupportedRequests =
        ["GetCapabilities", "GetTile", "GetFeatureInfo"];

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        OgcKvpValidator.ValidateService(q, "WMTS");
        var request = OgcKvpValidator.ValidateRequest(q, SupportedRequests);
        switch (request)
        {
            case "GetCapabilities":
            {
                var sections = WmtsConformance.ParseSections(OgcRequestParser.Get(q, "SECTIONS"));
                var xml = await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug,
                    sections?.Count > 0 ? (System.Collections.Generic.IReadOnlyCollection<string>)sections : null,
                    folderPath);
                return Content(xml, "text/xml; charset=utf-8");
            }
            case "GetTile":
            {
                var layer = RequireParam(q, "LAYER", "layer");
                var style = RequireParam(q, "STYLE", "style");
                var format = RequireParam(q, "FORMAT", "format");
                var tms = RequireParam(q, "TILEMATRIXSET", "tileMatrixSet");
                var zRaw = RequireParam(q, "TILEMATRIX", "tileMatrix");
                var rowRaw = RequireParam(q, "TILEROW", "tileRow");
                var colRaw = RequireParam(q, "TILECOL", "tileCol");

                WmtsConformance.ValidateStyle(style);
                WmtsConformance.ValidateTileMatrixSet(tms);

                var z = ParseIntOrThrow(zRaw, "tileMatrix");
                var row = ParseIntOrThrow(rowRaw, "tileRow");
                var col = ParseIntOrThrow(colRaw, "tileCol");

                var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, style, tms, z, col, row, format);
                return File(bytes, format);
            }
            default:
                // ValidateRequest already filtered to SupportedRequests; unreachable for valid inputs.
                throw new OgcException("OperationNotSupported",
                    $"WMTS REQUEST '{request}' not implemented", 501, "request");
        }
    }

    private static string RequireParam(Microsoft.AspNetCore.Http.IQueryCollection q, string key, string locator)
    {
        var v = OgcRequestParser.Get(q, key);
        if (string.IsNullOrWhiteSpace(v))
            throw new OgcException("MissingParameterValue",
                $"Missing required parameter '{locator}'", 400, locator);
        return v;
    }

    private static int ParseIntOrThrow(string raw, string locator)
    {
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
            throw new OgcException("InvalidParameterValue",
                $"Parameter '{locator}' value '{raw}' is not a non-negative integer", 400, locator);
        return v;
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
        var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, style, tms, z, x, y, format);
        return File(bytes, format);
    }
}
