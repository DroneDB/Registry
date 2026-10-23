#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The entries of a dataset (files and folders): listings, search, reading, moving, copying, transferring
/// and deleting them, and their thumbnails and map tiles. Uploads, downloads, builds, raster analysis and
/// stockpiles have controllers of their own on the same route prefix.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug)]
[Produces("application/json")]
public class ObjectsController : ControllerBaseEx
{
    private readonly IEntryQueryService _queries;
    private readonly IEntryCommandService _commands;
    private readonly IEntryTransferService _transfers;
    private readonly IObjectRenderService _renders;
    private readonly ILogger<ObjectsController> _logger;
    private readonly ThumbnailOptions _thumbnails;

    public ObjectsController(IEntryQueryService queries, IEntryCommandService commands, IEntryTransferService transfers,
        IObjectRenderService renders, ILogger<ObjectsController> logger, IOptions<ThumbnailOptions> thumbnails)
    {
        _queries = queries;
        _commands = commands;
        _transfers = transfers;
        _renders = renders;
        _logger = logger;
        _thumbnails = thumbnails.Value;
    }

    /// <summary>
    /// Generates a thumbnail for a specific object.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to the object.</param>
    /// <param name="size">Optional thumbnail size.</param>
    /// <param name="v">Optional entry hash; when it matches the entry, the response may be cached without revalidation.</param>
    /// <returns>The thumbnail image file; 404 when the entry's type has no thumbnail (libddb's entry types) or its
    /// build artifact does not exist yet.</returns>
    [HttpGet("thumb", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnail))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateThumbnail(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string? path,
        [FromQuery] int? size,
        [FromQuery] string? v = null, CancellationToken ct = default)
    {
        StorageDataDto? res;
        HashedContentCachePolicy? cachePolicy = null;

        // Dataset thumbnails (no path) are not tied to one entry hash: no validator.
        if (string.IsNullOrWhiteSpace(path))
        {
            res = await _renders.GenerateThumbnailDataAsync(orgSlug, dsSlug, path, size, ct: ct);
        }
        else
        {
            var obj = await _renders.ResolveThumbnailObjectAsync(orgSlug, dsSlug,
                path.StartsWith('/') ? path[1..] : path, ct: ct);

            cachePolicy = HashedContentCachePolicy.For(obj.Hash, v, "thumb",
                size ?? _thumbnails.DefaultThumbnailSize, obj.SourceVersion);
            if (cachePolicy.IsNotModified(Request))
                return NotModified(cachePolicy);

            res = await _renders.GenerateThumbnailDataAsync(obj, size, ct: ct);
        }

        if (res == null)
        {
            _logger.LogWarning("Thumbnail generation returned null for '{OrgSlug}/{DsSlug}' path: '{Path}'",
                orgSlug, dsSlug, path);
            return NotFound();
        }

        _logger.LogInformation(
            "Successfully generated thumbnail for '{OrgSlug}/{DsSlug}' path: '{Path}', size: {DataSize} bytes",
            orgSlug, dsSlug, path, res.Data.Length);

        cachePolicy?.Apply(Response);
        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>
    /// Generates a map tile for a specific object.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="tz">The tile zoom level.</param>
    /// <param name="tx">The tile X coordinate.</param>
    /// <param name="tyRaw">The tile Y coordinate (supports @2x retina suffix).</param>
    /// <param name="path">The path to the object.</param>
    /// <param name="ext">The tile image extension (png or webp).</param>
    /// <param name="v">Optional entry hash; when it matches the entry, the response may be cached without revalidation.</param>
    /// <returns>The tile image file.</returns>
    [HttpGet("tiles/{tz:int}/{tx:int}/{tyRaw}.{ext:regex(^(png|webp)$)}",
        Name = nameof(ObjectsController) + "." + nameof(GenerateTile))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateTile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] int tz,
        [FromRoute, Required] int tx,
        [FromRoute, Required] string tyRaw,
        [FromQuery] string? path,
        [FromRoute, Required] string ext,
        [FromQuery] string? v = null, CancellationToken ct = default)
    {
        var retina = tyRaw.EndsWith("@2x", StringComparison.Ordinal);

        if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
            throw new BadRequestException("Invalid input parameters (retina indicator should be '@2x')");

        // Access check and lookup only; the 304 below skips the cache and the native render.
        var obj = await _renders.ResolveReadableObjectAsync(orgSlug, dsSlug, path, ct: ct);

        var cachePolicy = HashedContentCachePolicy.For(obj.Hash, v, "tile", tz, tx, ty, retina ? 2 : 1,
            obj.SourceVersion);
        if (cachePolicy.IsNotModified(Request))
            return NotModified(cachePolicy);

        var res = await _renders.GenerateTileDataAsync(obj, tz, tx, ty, retina, ct: ct);

        cachePolicy.Apply(Response);
        return File(res.Data, res.ContentType, res.Name);
    }

    private StatusCodeResult NotModified(HashedContentCachePolicy cachePolicy)
    {
        cachePolicy.Apply(Response);
        return StatusCode(StatusCodes.Status304NotModified);
    }

    /// <summary>
    /// Gets an object from a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to the object.</param>
    /// <returns>The object file.</returns>
    [HttpGet(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + "." + nameof(Get))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string? path, CancellationToken ct)
    {
        var res = await _queries.GetAsync(orgSlug, dsSlug, path, ct: ct);
        return PhysicalFile(res.PhysicalPath, res.ContentType, res.Name);
    }

    /// <summary>
    /// Lists objects in a dataset at a specific path.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to list.</param>
    /// <param name="type">Optional filter by entry type.</param>
    /// <returns>A list of entries.</returns>
    [HttpGet("list", Name = nameof(ObjectsController) + "." + nameof(GetInfo))]
    [ProducesResponseType(typeof(IEnumerable<EntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string? path,
        [FromQuery] EntryType? type = null, CancellationToken ct = default)
    {
        var res = await _queries.ListAsync(orgSlug, dsSlug, path, false, type, ct: ct);
        return Ok(res);
    }

    /// <summary>
    /// Lists objects in a dataset at a specific path (POST version).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to list.</param>
    /// <param name="type">Optional filter by entry type.</param>
    /// <returns>A list of entries.</returns>
    [HttpPost("list", Name = nameof(ObjectsController) + "." + nameof(GetInfoEx))]
    [ProducesResponseType(typeof(IEnumerable<EntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInfoEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string? path,
        [FromForm] EntryType? type = null, CancellationToken ct = default)
    {
        var res = await _queries.ListAsync(orgSlug, dsSlug, path, false, type, ct: ct);
        return Ok(res);
    }

    /// <summary>
    /// Searches for objects in a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="query">The search query.</param>
    /// <param name="path">The path to search in.</param>
    /// <param name="recursive">Whether to search recursively.</param>
    /// <param name="type">Optional filter by entry type.</param>
    /// <returns>A list of matching entries.</returns>
    [HttpPost("search", Name = nameof(ObjectsController) + "." + nameof(Search))]
    [ProducesResponseType(typeof(IEnumerable<EntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string? query,
        [FromForm] string? path,
        [FromForm] bool recursive = true,
        [FromForm] EntryType? type = null, CancellationToken ct = default)
    {
        var res = await _queries.SearchAsync(orgSlug, dsSlug, query, path, recursive, type, ct: ct);
        return Ok(res);
    }

    /// <summary>
    /// Deletes an object from a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to the object to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + "." + nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string? path, CancellationToken ct)
    {
        // No path is the empty path (the path rule refuses it)
        await _commands.DeleteAsync(orgSlug, dsSlug, [path ?? string.Empty], ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Deletes multiple objects from a dataset in a single batch operation.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="paths">Array of paths to delete.</param>
    /// <returns>A response containing the list of successfully deleted paths and any failures.</returns>
    [HttpDelete(RoutesHelper.ObjectsRadix + "/batch", Name = nameof(ObjectsController) + "." + nameof(DeleteBatch))]
    [ProducesResponseType(typeof(DeleteBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteBatch(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string[]? paths, CancellationToken ct)
    {
        if (paths == null || paths.Length == 0)
            throw new BadRequestException("No paths provided");

        var response = await _commands.DeleteBatchAsync(orgSlug, dsSlug, paths, ct: ct);
        return Ok(response);
    }

    /// <summary>
    /// Moves or renames an object within a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="source">The source path.</param>
    /// <param name="dest">The destination path.</param>
    /// <returns>The updated entry on success.</returns>
    [HttpPut(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + "." + nameof(Move))]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Move(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string? source,
        [FromForm] string? dest, CancellationToken ct)
    {
        var entry = await _commands.MoveAsync(orgSlug, dsSlug, source, dest, ct: ct);
        return Ok(entry);
    }

    /// <summary>
    /// Transfers an object from one dataset to another.
    /// </summary>
    /// <param name="orgSlug">The source organization slug.</param>
    /// <param name="dsSlug">The source dataset slug.</param>
    /// <param name="sourcePath">The source path.</param>
    /// <param name="destOrgSlug">The destination organization slug.</param>
    /// <param name="destDsSlug">The destination dataset slug.</param>
    /// <param name="destPath">The destination path (optional).</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("transfer", Name = nameof(ObjectsController) + "." + nameof(Transfer))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Transfer(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string sourcePath,
        [FromForm, Required] string destOrgSlug,
        [FromForm, Required] string destDsSlug,
        [FromForm] string? destPath = null,
        [FromForm] bool overwrite = false,
        [FromForm] bool keepSource = false, CancellationToken ct = default)
    {
        await _transfers.TransferAsync(orgSlug, dsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite, keepSource, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Copies a file or folder within the same dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="source">The source path (existing entry).</param>
    /// <param name="dest">The destination path.</param>
    /// <param name="overwrite">Whether to overwrite an existing destination.</param>
    /// <returns>The created destination entry.</returns>
    [HttpPost(RoutesHelper.ObjectsRadix + "/copy", Name = nameof(ObjectsController) + "." + nameof(Copy))]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Copy(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string source,
        [FromForm, Required] string dest,
        [FromForm] bool overwrite = false, CancellationToken ct = default)
    {
        var entry = await _transfers.CopyAsync(orgSlug, dsSlug, source, dest, overwrite, ct: ct);
        return Ok(entry);
    }
}
