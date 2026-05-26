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

namespace Registry.Web.Controllers;

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
