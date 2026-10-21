using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Services.Managers;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers;

/// <summary>
/// Mapbox Vector Tile (MVT) endpoint. Serves precomputed tiles from
/// {dataset}/.ddb/build/{hash}/mvt/{z}/{x}/{y}.pbf for the Vue/OpenLayers frontend
/// and OGC API Tiles delegate. Authorized like the OGC endpoints: Basic credentials, then
/// <see cref="OgcAuthorizationFilter"/> (read access, its denials in the OGC envelope).
/// </summary>
[ApiController]
[BasicAuthentication]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug + "/mvt")]
[Tags("MVT")]
public class MvtController : ControllerBaseEx
{
    private readonly IDatasetAccessResolver _access;
    private readonly IBuildArtifactResolver _artifacts;

    public MvtController(IDatasetAccessResolver access, IBuildArtifactResolver artifacts)
    {
        _access = access;
        _artifacts = artifacts;
    }

    [HttpGet("{hash}/{z:int}/{x:int}/{y:int}.pbf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)] // OGC exception XML (OgcAuthorizationFilter)
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] int z, [FromRoute] int x, [FromRoute] int y)
    {
        if (z < 0 || z > 24 || x < 0 || y < 0)
            throw new BadRequestException("Invalid tile coordinates");

        // The filter granted read access: the resolver reuses the grant
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read)).Ddb;
        var tilePath = _artifacts.GetMvtTilePath(ddb, hash, z, x, y);

        if (!_artifacts.ArtifactExists(tilePath))
            return NotFound();

        // Tiles are already gzip-compressed (COMPRESS=YES in MVT driver).
        Response.Headers.ContentEncoding = "gzip";
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers.ETag = $"\"{hash}-{z}-{x}-{y}\"";
        return PhysicalFile(tilePath, "application/vnd.mapbox-vector-tile");
    }

    [HttpGet("{hash}/metadata.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)] // OGC exception XML (OgcAuthorizationFilter)
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetadata(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read)).Ddb;
        var path = _artifacts.GetMvtMetadataPath(ddb, hash);
        if (!_artifacts.ArtifactExists(path)) return NotFound();
        return PhysicalFile(path, "application/json");
    }
}
