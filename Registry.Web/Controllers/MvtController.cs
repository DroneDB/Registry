using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Services.Managers;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers;

/// <summary>
/// Mapbox Vector Tiles (MVT) of a vector file: the precomputed tiles of its build and their metadata. Authorized
/// like the OGC services: Basic credentials or a token, and read access to the dataset (denials in the OGC envelope).
/// </summary>
/// <remarks>
/// Serves the <c>vector.mvt</c> artifact of libddb's build manifest (<see cref="MvtTiles"/>) for the Hub and the
/// OGC API Tiles delegate; <see cref="OgcAuthorizationFilter"/> checks the access.
/// </remarks>
[ApiController]
[BasicAuthentication]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[Route(RoutesHelper.DatasetPrefix + "/mvt")]
[Tags("MVT")]
public class MvtController : ControllerBaseEx
{
    /// <summary>How long a tile of a build may be reused: it never changes (its URL names the build).</summary>
    private const string TileFreshness = "max-age=31536000, immutable";

    private readonly IDatasetAccessResolver _access;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IAuthManager _auth;
    private readonly IFileSystem _fs;

    public MvtController(IDatasetAccessResolver access, IBuildArtifactResolver artifacts, IAuthManager auth,
        IFileSystem fs)
    {
        _access = access;
        _artifacts = artifacts;
        _auth = auth;
        _fs = fs;
    }

    /// <summary>A vector tile of a vector file's build, or 204 No Content for an empty tile of its pyramid.</summary>
    /// <remarks>
    /// A tile is a gzip-compressed Mapbox Vector Tile. GDAL writes no file for a tile of the pyramid without features:
    /// such a tile is 204, since a 404 would make every map log an error for the tiles around the data. A file
    /// without a pyramid, or a tile off its zoom levels or grid, is 404.
    /// </remarks>
    [HttpGet("{hash}/{z:int}/{x:int}/{y:int}.pbf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)] // OGC exception XML (OgcAuthorizationFilter)
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] int z, [FromRoute] int x, [FromRoute] int y, CancellationToken ct)
    {
        if (z < 0 || z > 24 || x < 0 || y < 0)
            throw new BadRequestException("Invalid tile coordinates");

        // The filter granted read access: the resolver reuses the grant
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var tilePath = _artifacts.FindArtifactFile(access.Ddb, hash, ArtifactRoles.VectorMvt, MvtTiles.Tile(z, x, y));
        if (tilePath == null)
        {
            var pyramid = _artifacts.FindArtifactFile(access.Ddb, hash, ArtifactRoles.VectorMvt);
            if (pyramid == null || !MvtTiles.Covers(await ReadDescriptor(pyramid, ct), z, x, y))
                return NotFound();

            // An empty tile of a build is as lasting as a full one
            await SetTileCacheControl(access);
            return NoContent();
        }

        // Tiles are already gzip-compressed (COMPRESS=YES in MVT driver). The tag names the tile of a build: it never
        // changes, so a client that has it gets a 304
        Response.Headers.ContentEncoding = "gzip";
        await SetTileCacheControl(access);
        return ValidatedPhysicalFile(tilePath, "application/vnd.mapbox-vector-tile", $"\"{hash}-{z}-{x}-{y}\"");
    }

    // The text of a pyramid's descriptor, or null when it went away since it was found (a rebuild)
    private async Task<string?> ReadDescriptor(string path, CancellationToken ct)
    {
        try
        {
            return await _fs.ReadAllTextAsync(path, ct);
        }
        catch (IOException)
        {
            return null;
        }
    }

    // A tile of a build never changes. A shared cache may keep it only when anyone may read the dataset: a tile of a
    // private dataset is the caller's only
    private async Task SetTileCacheControl(DatasetAccess access) =>
        Response.Headers.CacheControl =
            (await _auth.IsReadableAnonymously(access.Dataset) ? "public, " : "private, ") + TileFreshness;

    /// <summary>The metadata (metadata.json) of the vector tiles of a vector file's build.</summary>
    [HttpGet("{hash}/metadata.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)] // OGC exception XML (OgcAuthorizationFilter)
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetadata(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash, CancellationToken ct)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;
        // The pyramid's descriptor (metadata.json)
        var path = _artifacts.FindArtifactFile(ddb, hash, ArtifactRoles.VectorMvt);
        if (path == null) return NotFound();
        return PhysicalFile(path, "application/json");
    }
}
