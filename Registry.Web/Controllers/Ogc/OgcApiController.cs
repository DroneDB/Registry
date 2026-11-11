using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Ports.DroneDB;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers.Ogc;

/// <summary>OGC API – Features + Tiles controller (JSON REST).</summary>
[ApiController]
[BasicAuthentication]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status404NotFound, "text/xml", "application/xml")]
[ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable, "text/xml", "application/xml")]
[Route(RoutesHelper.DatasetPrefix + "/ogcapi")]
public class OgcApiController : ControllerBaseEx
{
    private readonly OgcApiFeaturesManager _features;
    private readonly OgcApiTilesManager _tiles;
    private readonly IOgcLayerCatalog _layers;

    public OgcApiController(OgcApiFeaturesManager features, OgcApiTilesManager tiles, IOgcLayerCatalog layers)
    {
        _features = features; _tiles = tiles; _layers = layers;
    }

    private string BaseUrl([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/orgs/{orgSlug}/ds/{dsSlug}/ogcapi";

    /// <summary>The landing page: the links of the API.</summary>
    [HttpGet("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OgcApiLandingDto>> Landing([FromRoute] string orgSlug, [FromRoute] string dsSlug, CancellationToken ct)
        => await _features.GetLandingAsync(orgSlug, dsSlug, BaseUrl(orgSlug, dsSlug), ct: ct);

    /// <summary>The conformance classes the API declares.</summary>
    [HttpGet("conformance")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OgcConformanceDto>> Conformance(CancellationToken ct)
        => await _features.GetConformanceAsync();

    /// <summary>The collections (the vector layers of the dataset).</summary>
    [HttpGet("collections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OgcApiCollectionsDto>> Collections([FromRoute] string orgSlug, [FromRoute] string dsSlug, CancellationToken ct)
        => await _features.GetCollectionsAsync(orgSlug, dsSlug, BaseUrl(orgSlug, dsSlug), ct: ct);

    /// <summary>One collection.</summary>
    [HttpGet("collections/{collectionId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OgcApiCollectionDto>> Collection([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, CancellationToken ct)
    {
        var col = await _features.GetCollectionAsync(orgSlug, dsSlug, collectionId, BaseUrl(orgSlug, dsSlug), ct: ct);
        return col == null ? NotFound() : col;
    }

    /// <summary>The features of a collection, as a GeoJSON feature collection.</summary>
    [HttpGet("collections/{collectionId}/items")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "application/geo+json")]
    public async Task<IActionResult> Items([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId,
        [FromQuery] string? bbox = null,
        [FromQuery] int limit = 10,
        [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        double[]? bboxArr = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parsed = OgcRequestParser.ParseBbox(bbox, "CRS:84", null);
            bboxArr = parsed.Bbox;
        }
        var json = await _features.GetItemsAsync(orgSlug, dsSlug, collectionId, bboxArr, limit, offset, ct: ct);
        return Content(json, "application/geo+json");
    }

    /// <summary>One feature of a collection, as GeoJSON.</summary>
    [HttpGet("collections/{collectionId}/items/{featureId}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "application/geo+json")]
    public async Task<IActionResult> Item([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, [FromRoute] string featureId, CancellationToken ct)
    {
        var json = await _features.GetItemAsync(orgSlug, dsSlug, collectionId, featureId, ct: ct);
        return Content(json, "application/geo+json");
    }

    /// <summary>The tile sets of a collection.</summary>
    [HttpGet("collections/{collectionId}/tiles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OgcApiTileSetsDto>> TileSets([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, CancellationToken ct)
        => await _tiles.GetTileSetsAsync(orgSlug, dsSlug, collectionId, BaseUrl(orgSlug, dsSlug), ct: ct);

    /// <summary>One tile of a collection: a vector tile for vector layers, a PNG for rasters.</summary>
    [HttpGet("collections/{collectionId}/tiles/{tileMatrixSet}/{z:int}/{y:int}/{x:int}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "application/vnd.mapbox-vector-tile", "image/png")]
    public async Task<IActionResult> Tile([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, [FromRoute] string tileMatrixSet,
        [FromRoute] int z, [FromRoute] int y, [FromRoute] int x, CancellationToken ct)
    {
        var bytes = await _tiles.GetTileAsync(orgSlug, dsSlug, collectionId, tileMatrixSet, z, x, y, ct: ct);
        if (bytes == null) return NotFound();

        // Choose the media type from the layer kind: vector layers serve MVT, rasters serve PNG.
        var layer = await _layers.ResolveAsync(orgSlug, dsSlug, System.Uri.UnescapeDataString(collectionId), ct: ct);
        var contentType = layer?.EntryType == EntryType.Vector
            ? "application/vnd.mapbox-vector-tile"
            : "image/png";
        return File(bytes, contentType);
    }
}
