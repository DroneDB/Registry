using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Common.Model;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Services.Managers;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Mapbox Vector Tile (MVT) endpoint. Serves precomputed tiles from
/// {dataset}/.ddb/build/{hash}/mvt/{z}/{x}/{y}.pbf for the Vue/OpenLayers frontend
/// and OGC API Tiles delegate.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug + "/mvt")]
[Tags("MVT")]
public class MvtController : ControllerBaseEx
{
    private readonly IUtils _utils;
    private readonly IAuthManager _authManager;
    private readonly IDdbManager _ddbManager;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IFileSystem _fs;
    private readonly ILogger<MvtController> _logger;

    public MvtController(IUtils utils, IAuthManager authManager, IDdbManager ddbManager,
        IBuildArtifactResolver artifacts, IFileSystem fs, ILogger<MvtController> logger)
    {
        _utils = utils;
        _authManager = authManager;
        _ddbManager = ddbManager;
        _artifacts = artifacts;
        _fs = fs;
        _logger = logger;
    }

    [ServiceFilter(typeof(BasicAuthFilter))]
    [HttpGet("{hash}/{z:int}/{x:int}/{y:int}.pbf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] int z, [FromRoute] int x, [FromRoute] int y)
    {
        try
        {
            if (z < 0 || z > 24 || x < 0 || y < 0)
                return BadRequest(new ErrorResponse("Invalid tile coordinates"));

            var ds = _utils.GetDataset(orgSlug, dsSlug);
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
            {
                BasicAuthFilter.SendBasicAuthRequest(Response);
                throw new UnauthorizedException("Authentication required");
            }

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var tilePath = _artifacts.GetMvtTilePath(ddb, hash, z, x, y);

            if (!_artifacts.ArtifactExists(tilePath))
                return NotFound();

            // Tiles are already gzip-compressed (COMPRESS=YES in MVT driver).
            Response.Headers.ContentEncoding = "gzip";
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            Response.Headers.ETag = $"\"{hash}-{z}-{x}-{y}\"";
            return PhysicalFile(tilePath, "application/vnd.mapbox-vector-tile");
        }
        catch (UnauthorizedException ex)
        {
            BasicAuthFilter.SendBasicAuthRequest(Response);
            return ExceptionResult(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MVT GetTile {Org}/{Ds} {Hash} z={Z}", orgSlug, dsSlug, hash, z);
            return ExceptionResult(ex);
        }
    }

    [ServiceFilter(typeof(BasicAuthFilter))]
    [HttpGet("{hash}/metadata.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetadata(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash)
    {
        try
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
            {
                BasicAuthFilter.SendBasicAuthRequest(Response);
                throw new UnauthorizedException("Authentication required");
            }

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var path = _artifacts.GetMvtMetadataPath(ddb, hash);
            if (!_artifacts.ArtifactExists(path)) return NotFound();
            return PhysicalFile(path, "application/json");
        }
        catch (UnauthorizedException ex)
        {
            BasicAuthFilter.SendBasicAuthRequest(Response);
            return ExceptionResult(ex);
        }
        catch (Exception ex)
        {
            return ExceptionResult(ex);
        }
    }
}
