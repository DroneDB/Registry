#nullable enable
using System;
using System.Threading;
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

namespace Registry.Web.Controllers.Ogc;

/// <summary>WMTS 1.0.0 controller - KVP + RESTful tile retrieval.</summary>
[ApiController]
[BasicAuthentication]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status404NotFound, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable, "text/xml", "application/xml")]
[Route(RoutesHelper.DatasetPrefix)]
public class WmtsController : ControllerBaseEx
{
    private readonly IWmtsManager _mgr;

    public WmtsController(IWmtsManager mgr)
    {
        _mgr = mgr;
    }

    /// <summary>WMTS 1.0.0 KVP request: GetCapabilities (XML), GetTile (an image or a vector tile) or GetFeatureInfo.</summary>
    [HttpGet("wmts")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/xml", "application/xml", "image/png", "image/jpeg", "application/vnd.mapbox-vector-tile")]
    public async Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug, CancellationToken ct)
        => await Dispatch(orgSlug, dsSlug, folderPath: null, ct);

    /// <summary>WMTS KVP request for the layers of a folder of the dataset.</summary>
    [HttpGet("wmts/p/{*folder}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/xml", "application/xml", "image/png", "image/jpeg", "application/vnd.mapbox-vector-tile")]
    public async Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder, CancellationToken ct) => await Dispatch(orgSlug, dsSlug, folder, ct);

    private static readonly string[] SupportedRequests =
        ["GetCapabilities", "GetTile", "GetFeatureInfo"];

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath, CancellationToken ct)
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
                    folderPath, ct: ct);
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

                var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, style, tms, z, col, row, format, ct: ct);
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

    /// <summary>WMTS 1.0.0 RESTful tile.</summary>
    [HttpGet("wmts/1.0.0/{layer}/{style}/{tms}/{z:int}/{y:int}/{x:int}.{ext:regex(^(pbf|png|jpe?g)$)}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "image/png", "image/jpeg", "application/vnd.mapbox-vector-tile")]
    public async Task<IActionResult> Restful([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string layer, [FromRoute] string style, [FromRoute] string tms,
        [FromRoute] int z, [FromRoute] int y, [FromRoute] int x, [FromRoute] string ext, CancellationToken ct)
    {
        var format = ext.ToLowerInvariant() switch
        {
            "pbf" => "application/vnd.mapbox-vector-tile",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            _ => "image/png"
        };
        // Route values may arrive percent-encoded when the layer identifier contains '/' (e.g. "folder%2Ffile.shp").
        layer = Uri.UnescapeDataString(layer);
        var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, style, tms, z, x, y, format, ct: ct);
        return File(bytes, format);
    }
}
