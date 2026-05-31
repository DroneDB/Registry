using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Ports.DroneDB;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers.Ogc;

/// <summary>OGC API – Features + Tiles controller (JSON REST).</summary>
[ApiController]
[ServiceFilter(typeof(BasicAuthFilter))]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug + "/ogcapi")]
public class OgcApiController : ControllerBaseEx
{
    private readonly IOgcApiFeaturesManager _features;
    private readonly IOgcApiTilesManager _tiles;
    private readonly IOgcLayerCatalog _layers;

    public OgcApiController(IOgcApiFeaturesManager features, IOgcApiTilesManager tiles, IOgcLayerCatalog layers)
    {
        _features = features; _tiles = tiles; _layers = layers;
    }

    private string BaseUrl([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/orgs/{orgSlug}/ds/{dsSlug}/ogcapi";

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

        // Choose the media type from the layer kind: vector layers serve MVT, rasters serve PNG.
        var layer = await _layers.ResolveAsync(orgSlug, dsSlug, System.Uri.UnescapeDataString(collectionId));
        var contentType = layer?.EntryType == EntryType.Vector
            ? "application/vnd.mapbox-vector-tile"
            : "image/png";
        return File(bytes, contentType);
    }
}
