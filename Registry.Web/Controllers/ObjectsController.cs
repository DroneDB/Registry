using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Microsoft.VisualBasic.CompilerServices;
using MimeMapping;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Filters;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Adapters;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing objects (files and folders) within datasets.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug)]
[Produces("application/json")]
public class ObjectsController : ControllerBaseEx
{
    private readonly IObjectsManager _objectsManager;
    private readonly ILogger<ObjectsController> _logger;
    private readonly FormOptions _formOptions;
    private readonly ThumbnailOptions _thumbnails;
    private readonly DatasetPathPolicy _paths;

    public ObjectsController(IObjectsManager datasetsManager, ILogger<ObjectsController> logger,
        IOptions<FormOptions> formOptions, IOptions<ThumbnailOptions> thumbnails, DatasetPathPolicy paths)
    {
        _objectsManager = datasetsManager;
        _logger = logger;
        _formOptions = formOptions.Value;
        _thumbnails = thumbnails.Value;
        _paths = paths;
    }

    // libddb's path rule before the manager runs (RQ-11 replaces these with a DatasetPath model binder)
    private void EnsureValidPath(string path) => _paths.Normalize(path);

    private void EnsureValidPaths(IReadOnlyCollection<string>? paths, string? outputPath = null)
    {
        if (paths == null || paths.Count == 0)
            throw new BadRequestException("paths is required");
        foreach (var path in paths)
            _paths.Normalize(path, "input path");
        if (outputPath != null)
            _paths.Normalize(outputPath, "output path");
    }

    /// <summary>
    /// Downloads the DroneDB database file for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The DDB file as a downloadable attachment.</returns>
    [HttpGet("ddb", Name = nameof(ObjectsController) + "." + nameof(GetDdb))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDdb(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        CancellationToken cancellationToken)
    {
        try
        {
            var res = await _objectsManager.GetDdb(orgSlug, dsSlug);
            return await SendDownload(res, $"attachment; filename=\"{res.Name}\"", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - not an error
            _logger.LogDebug("Download cancelled by client: GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Generates a thumbnail for a specific object.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to the object.</param>
    /// <param name="size">Optional thumbnail size.</param>
    /// <param name="v">Optional entry hash; when it matches the entry, the response may be cached without revalidation.</param>
    /// <returns>The thumbnail image file.</returns>
    [HttpGet("thumb", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnail))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateThumbnail(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string path,
        [FromQuery] int? size,
        [FromQuery] string v = null)
    {
        StorageDataDto res;
        HashedContentCachePolicy cachePolicy = null;

        // Dataset thumbnails (no path) are not tied to one entry hash: no validator.
        if (string.IsNullOrWhiteSpace(path))
        {
            res = await _objectsManager.GenerateThumbnailData(orgSlug, dsSlug, path, size);
        }
        else
        {
            var obj = await _objectsManager.ResolveReadableObject(orgSlug, dsSlug,
                path.StartsWith('/') ? path[1..] : path);

            cachePolicy = HashedContentCachePolicy.For(obj.Hash, v, "thumb",
                size ?? _thumbnails.DefaultThumbnailSize, obj.SourceVersion);
            if (cachePolicy.IsNotModified(Request))
                return NotModified(cachePolicy);

            res = await _objectsManager.GenerateThumbnailData(obj, size);
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
        [FromQuery] string path,
        [FromRoute, Required] string ext,
        [FromQuery] string v = null)
    {
        var retina = tyRaw.EndsWith("@2x");

        if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
            throw new BadRequestException("Invalid input parameters (retina indicator should be '@2x')");

        // Access check and lookup only; the 304 below skips the cache and the native render.
        var obj = await _objectsManager.ResolveReadableObject(orgSlug, dsSlug, path);

        var cachePolicy = HashedContentCachePolicy.For(obj.Hash, v, "tile", tz, tx, ty, retina ? 2 : 1,
            obj.SourceVersion);
        if (cachePolicy.IsNotModified(Request))
            return NotModified(cachePolicy);

        var res = await _objectsManager.GenerateTileData(obj, tz, tx, ty, retina);

        cachePolicy.Apply(Response);
        return File(res.Data, res.ContentType, res.Name);
    }

    private StatusCodeResult NotModified(HashedContentCachePolicy cachePolicy)
    {
        cachePolicy.Apply(Response);
        return StatusCode(StatusCodes.Status304NotModified);
    }

    #region Downloads

    // Writes a download straight to the response as it is produced (REG-04): no Content-Length
    // (chunked), no buffering on the way out, and the work stops when the client goes away
    private async Task<IActionResult> SendDownload(FileStreamDescriptor download, string contentDisposition,
        CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = download.ContentType;
        Response.Headers.Append("Content-Disposition", contentDisposition);
        Response.Headers.CacheControl = "no-store";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await download.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    private async Task<IActionResult> InternalDownload(string orgSlug, string dsSlug, string[] paths, bool isInline,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // If only one file is requested, we can leverage the local file system
            if (paths?.Length == 1 &&
                await _objectsManager.GetEntryType(orgSlug, dsSlug, paths[0]) != EntryType.Directory)
            {
                var r = await _objectsManager.Get(orgSlug, dsSlug, paths[0]);
                return PhysicalFile(r.PhysicalPath, r.ContentType, isInline ? null : r.Name, true);
            }

            var res = await _objectsManager.DownloadStream(orgSlug, dsSlug, paths);
            return await SendDownload(res, isInline ? "inline" : $"attachment; filename=\"{res.Name}\"",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - not an error
            _logger.LogDebug(
                "Download cancelled by client: Download('{OrgSlug}', '{DsSlug}', '{Paths}')",
                orgSlug, dsSlug, paths != null ? string.Join("; ", paths) : string.Empty);
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Downloads one or more objects from a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="pathsRaw">Paths to download: one or more "path" query parameters (each occurrence is
    /// a literal path, so file names may contain ',' and '&amp;') or a single legacy comma-separated value.</param>
    /// <param name="isInlineRaw">If 1, display inline instead of as attachment.</param>
    /// <returns>The file(s) as a downloadable stream.</returns>
    [HttpGet("download", Name = nameof(ObjectsController) + "." + nameof(Download))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Download(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery(Name = "path")] string pathsRaw,
        [FromQuery(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        // NOTE: read the raw query here instead of trusting the [FromQuery] string binding:
        // simple-type binding only sees the FIRST occurrence of a repeated parameter,
        // and an array binding would still split each value on commas.
        // With multiple ?path= occurrences every value is taken as a literal path;
        // with a single occurrence the legacy comma-split behaviour is preserved
        // so already-shared download links keep working.
        var rawPaths = Request.Query["path"];
        var paths = rawPaths.Count > 1
            ? rawPaths.Where(p => !string.IsNullOrEmpty(p)).Select(p => (string)p!).ToArray()
            : pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);
        var isInline = isInlineRaw == 1;


        return InternalDownload(orgSlug, dsSlug, paths, isInline, cancellationToken);
    }

    /// <summary>
    /// Downloads one or more objects from a dataset (POST version).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="pathsRaw">Comma-separated list of paths to download.</param>
    /// <param name="isInlineRaw">If 1, display inline instead of as attachment.</param>
    /// <returns>The file(s) as a downloadable stream.</returns>
    [HttpPost("download", Name = nameof(ObjectsController) + "." + nameof(DownloadPost))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> DownloadPost(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "path")] string pathsRaw,
        [FromForm(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        var paths = pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);
        var isInline = isInlineRaw == 1;


        return InternalDownload(orgSlug, dsSlug, paths, isInline, cancellationToken);
    }

    /// <summary>
    /// Downloads a specific object by its exact path.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The exact path to the object.</param>
    /// <param name="isInlineRaw">If 1, display inline instead of as attachment.</param>
    /// <returns>The file as a downloadable stream.</returns>
    [HttpGet("download/{*path}", Name = nameof(ObjectsController) + "." + nameof(DownloadExact))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadExact(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute] string path,
        [FromQuery(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        var isInline = isInlineRaw == 1;


        return await InternalDownload(orgSlug, dsSlug, [path], isInline, cancellationToken);
    }

    #endregion

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
        [FromForm] string path)
    {
        var res = await _objectsManager.Get(orgSlug, dsSlug, path);
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
        [FromQuery] string path,
        [FromQuery] EntryType? type = null)
    {
        var res = await _objectsManager.List(orgSlug, dsSlug, path, false, type);
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
        [FromForm] string path,
        [FromForm] EntryType? type = null)
    {
        var res = await _objectsManager.List(orgSlug, dsSlug, path, false, type);
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
        [FromForm] string query,
        [FromForm] string path,
        [FromForm] bool recursive = true,
        [FromForm] EntryType? type = null)
    {
        var res = await _objectsManager.Search(orgSlug, dsSlug, query, path, recursive, type);
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
        [FromForm] string path)
    {
        await _objectsManager.Delete(orgSlug, dsSlug, path);
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
        [FromForm] string[] paths)
    {
        if (paths == null || paths.Length == 0)
            throw new BadRequestException("No paths provided");

        var response = await _objectsManager.DeleteBatch(orgSlug, dsSlug, paths);
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
        [FromForm] string source,
        [FromForm] string dest)
    {
        var entry = await _objectsManager.Move(orgSlug, dsSlug, source, dest);
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
        [FromForm] string destPath = null,
        [FromForm] bool overwrite = false,
        [FromForm] bool keepSource = false)
    {
        await _objectsManager.Transfer(orgSlug, dsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite, keepSource);
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
        [FromForm] bool overwrite = false)
    {
        var entry = await _objectsManager.Copy(orgSlug, dsSlug, source, dest, overwrite);
        return Ok(entry);
    }

    /// <summary>
    /// Triggers a build process for an object.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to the object to build.</param>
    /// <param name="force">Whether to force rebuild.</param>
    /// <returns>Ok on success.</returns>
    [HttpPost("build", Name = nameof(ObjectsController) + "." + nameof(Build))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Build(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string path,
        [FromForm] bool force = false)
    {
        await _objectsManager.Build(orgSlug, dsSlug, path, force);
        return Ok();
    }

    /// <summary>
    /// Gets a file from a build by hash and path.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <returns>The build file.</returns>
    [BasicAuthentication]
    [HttpGet("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(GetBuildFile))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string path)
    {
        var res = await _objectsManager.GetBuildFile(orgSlug, dsSlug, hash, path);

        return PhysicalFile(res, MimeUtility.GetMimeMapping(res), true);
    }

    /// <summary>
    /// Checks if a build file exists by hash and path.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <returns>Ok if file exists, NotFound otherwise.</returns>
    [BasicAuthentication]
    [HttpHead("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(CheckBuildFile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string path)
    {
        var res = await _objectsManager.CheckBuildFile(orgSlug, dsSlug, hash, path);

        return res ? Ok() : NotFound();
    }

    /// <summary>
    /// Downloads a pre-built visualization artifact (COG, COPC, or GPKG) for a given entry.
    /// The entry type determines which artifact is served. Zero-copy streaming via PhysicalFileResult.
    /// </summary>
    [HttpGet("build-artifact", Name = nameof(ObjectsController) + "." + nameof(DownloadBuildArtifact))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadBuildArtifact(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string path)
    {
        EnsureValidPath(path);

        var (fullPath, contentType, fileName) =
            await _objectsManager.GetBuildArtifactPath(orgSlug, dsSlug, path);

        return PhysicalFile(fullPath, contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Gets information about pending (deferred) builds for a dataset, i.e.
    /// builds skipped because one or more dependencies (companion files or
    /// external tools) were missing at the time of the last attempt.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>A list of pending builds.</returns>
    [HttpGet("builds/pending", Name = nameof(ObjectsController) + "." + nameof(GetPendingBuilds))]
    [HttpGet("builds", Name = nameof(ObjectsController) + "." + nameof(GetPendingBuilds) + "Legacy")]
    [Obsolete("Use 'GET builds/pending' instead. This alias is kept for backward compatibility with " +
              "clients built against the pre-unified-viewer API (which returned BuildJobDto) and will " +
              "be removed in a future version.")]
    [ProducesResponseType(typeof(IEnumerable<PendingBuildInfoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        var pending = await _objectsManager.GetPendingBuilds(orgSlug, dsSlug);

        return Ok(pending);
    }

    /// <summary>
    /// Clears all completed build jobs for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The number of deleted build jobs.</returns>
    [HttpPost("builds/clear", Name = nameof(ObjectsController) + "." + nameof(ClearCompletedBuilds))]
    [ProducesResponseType(typeof(ClearCompletedBuildsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearCompletedBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        var deletedCount = await _objectsManager.ClearCompletedBuilds(orgSlug, dsSlug);

        return Ok(new ClearCompletedBuildsResponse { DeletedCount = deletedCount });
    }

    #region Multispectral

    /// <summary>Get raster info (bands, sensor profile, presets) for a file.</summary>
    [HttpGet("raster-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path)
    {
        EnsureValidPath(path);

        var json = await _objectsManager.GetRasterInfo(orgSlug, dsSlug, path);
        return Content(json, "application/json");
    }

    /// <summary>Get raster statistics/histogram for a band or formula.</summary>
    [HttpGet("raster-metadata", Name = nameof(ObjectsController) + "." + nameof(GetRasterMetadata))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterMetadata(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter)
    {
        EnsureValidPath(path);

        var json = await _objectsManager.GetRasterMetadata(orgSlug, dsSlug, path, formula, bandFilter);
        return Content(json, "application/json");
    }

    /// <summary>Generate thumbnail with extended visualization params.</summary>
    [HttpGet("thumb-ex", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnailEx))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateThumbnailEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery] int? size,
        [FromQuery] string? preset,
        [FromQuery] string? bands,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter,
        [FromQuery] string? colormap,
        [FromQuery] string? rescale)
    {
        EnsureValidPath(path);

        var res = await _objectsManager.GenerateThumbnailDataEx(orgSlug, dsSlug, path, size,
            preset, bands, formula, bandFilter, colormap, rescale);

        if (res == null) return NotFound();
        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Generate tile with extended visualization params.</summary>
    [HttpGet("tiles-ex/{tz:int}/{tx:int}/{tyRaw}.{ext:regex(^(png|webp)$)}",
        Name = nameof(ObjectsController) + "." + nameof(GenerateTileEx))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateTileEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] int tz,
        [FromRoute, Required] int tx,
        [FromRoute, Required] string tyRaw,
        [FromQuery, Required] string path,
        [FromRoute, Required] string ext,
        [FromQuery] string? preset,
        [FromQuery] string? bands,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter,
        [FromQuery] string? colormap,
        [FromQuery] string? rescale)
    {
        EnsureValidPath(path);

        var retina = tyRaw.EndsWith("@2x");
        if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
            throw new BadRequestException("Invalid input parameters (retina indicator should be '@2x')");

        var res = await _objectsManager.GenerateTileDataEx(orgSlug, dsSlug, path, tz, tx, ty, retina,
            preset, bands, formula, bandFilter, colormap, rescale);

        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Export raster with visualization params applied as GeoTIFF.</summary>
    [HttpGet("export", Name = nameof(ObjectsController) + "." + nameof(ExportRaster))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExportRaster(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery] string? preset = null,
        [FromQuery] string? bands = null,
        [FromQuery] string? formula = null,
        [FromQuery] string? bandFilter = null,
        [FromQuery] string? colormap = null,
        [FromQuery] string? rescale = null)
    {
        EnsureValidPath(path);

        var res = await _objectsManager.ExportRaster(orgSlug, dsSlug, path,
            preset, bands, formula, bandFilter, colormap, rescale);

        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Validate merge-multispectral inputs.</summary>
    [HttpPost("merge-multispectral/validate",
        Name = nameof(ObjectsController) + "." + nameof(ValidateMergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateMergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        EnsureValidPaths(request.Paths);

        var json = await _objectsManager.ValidateMergeMultispectral(orgSlug, dsSlug, request.Paths);
        return Content(json, "application/json");
    }

    /// <summary>Preview merge-multispectral result.</summary>
    [HttpPost("merge-multispectral/preview",
        Name = nameof(ObjectsController) + "." + nameof(PreviewMergeMultispectral))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewMergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        EnsureValidPaths(request.Paths);

        var data = await _objectsManager.PreviewMergeMultispectral(orgSlug, dsSlug, request.Paths,
            request.PreviewBands, request.ThumbSize ?? 512);
        return File(data, "image/webp", "preview.webp");
    }

    /// <summary>Merge single-band rasters into a multi-band COG.</summary>
    [HttpPost("merge-multispectral", Name = nameof(ObjectsController) + "." + nameof(MergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new BadRequestException("outputPath is required");

        EnsureValidPaths(request.Paths, request.OutputPath);

        await _objectsManager.MergeMultispectral(orgSlug, dsSlug, request.Paths, request.OutputPath);
        return Ok(new { ok = true });
    }

    /// <summary>Validate that source and reference rasters are compatible for alignment.</summary>
    [HttpPost("align/validate",
        Name = nameof(ObjectsController) + "." + nameof(ValidateAlignRaster))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateAlignRaster(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] AlignRasterRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            throw new BadRequestException("sourcePath is required");
        if (string.IsNullOrWhiteSpace(request.ReferencePath))
            throw new BadRequestException("referencePath is required");

        EnsureValidPaths([request.SourcePath, request.ReferencePath]);

        var json = await _objectsManager.ValidateAlignRaster(orgSlug, dsSlug,
            request.SourcePath, request.ReferencePath);
        return Content(json, "application/json");
    }

    #endregion

    #region Raster Analysis

    /// <summary>Get raster value info (min/max/unit/dimensions), including thermal calibration if applicable.</summary>
    [HttpGet("raster-value-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterValueInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterValueInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path)
    {
        EnsureValidPath(path);

        var json = await _objectsManager.GetRasterValueInfo(orgSlug, dsSlug, path);
        return Content(json, "application/json");
    }

    /// <summary>Get raster value (temperature/elevation/etc.) at a specific pixel location.</summary>
    [HttpGet("raster-point-value", Name = nameof(ObjectsController) + "." + nameof(GetRasterPointValue))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterPointValue(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery, Required] int x,
        [FromQuery, Required] int y)
    {
        EnsureValidPath(path);

        var json = await _objectsManager.GetRasterPointValue(orgSlug, dsSlug, path, x, y);
        return Content(json, "application/json");
    }

    /// <summary>Get raster value statistics for a rectangular area.</summary>
    [HttpGet("raster-area-stats", Name = nameof(ObjectsController) + "." + nameof(GetRasterAreaStats))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterAreaStats(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery, Required] int x0,
        [FromQuery, Required] int y0,
        [FromQuery, Required] int x1,
        [FromQuery, Required] int y1)
    {
        EnsureValidPath(path);

        var json = await _objectsManager.GetRasterAreaStats(orgSlug, dsSlug, path, x0, y0, x1, y1);
        return Content(json, "application/json");
    }

    /// <summary>Sample raster values along a GeoJSON LineString (WGS84) and return a profile.</summary>
    /// <remarks>
    /// Accepts the geometry via POST body to support arbitrarily long polylines.
    /// Returns JSON with equispaced samples containing distance (meters), value, lon, lat.
    /// </remarks>
    [HttpPost("raster-profile", Name = nameof(ObjectsController) + "." + nameof(GetRasterProfile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterProfile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] RasterProfileRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        if (string.IsNullOrWhiteSpace(request.LineString))
            throw new BadRequestException("lineString (GeoJSON LineString) is required");

        var samples = request.Samples ?? 256;
        var json = await _objectsManager.GetRasterProfile(orgSlug, dsSlug, request.Path,
            request.LineString, samples);
        return Content(json, "application/json");
    }

    #endregion

    #region Stockpile Volume

    /// <summary>
    /// Calculate cut/fill/net stockpile volume over a polygon on a DEM raster.
    /// </summary>
    [HttpPost("stockpile/calculate", Name = nameof(ObjectsController) + "." + nameof(CalculateStockpileVolume))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CalculateStockpileVolume(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] VolumeCalculationRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        if (string.IsNullOrWhiteSpace(request.Polygon))
            throw new BadRequestException("polygon (GeoJSON Polygon) is required");

        var json = await _objectsManager.CalculateVolume(orgSlug, dsSlug, request.Path,
            request.Polygon, request.BaseMethod, request.FlatElevation);

        return Content(json, "application/json");
    }

    /// <summary>
    /// Auto-detect a stockpile footprint starting from a click on the raster.
    /// </summary>
    [HttpPost("stockpile/detect", Name = nameof(ObjectsController) + "." + nameof(DetectStockpile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DetectStockpile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] StockpileDetectionRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        var radius = request.Radius ?? 50.0;
        var sensitivity = request.Sensitivity ?? 0.5f;
        if (sensitivity < 0f) sensitivity = 0f;
        if (sensitivity > 1f) sensitivity = 1f;
        if (!(radius > 0)) throw new BadRequestException("radius must be positive");
        if (!request.Lat.HasValue || !request.Lon.HasValue)
            throw new BadRequestException("lat and lon are required");

        var json = await _objectsManager.DetectStockpile(orgSlug, dsSlug, request.Path,
            request.Lat.Value, request.Lon.Value, radius, sensitivity);
        return Content(json, "application/json");
    }

    /// <summary>
    /// Auto-detect all stockpile footprints in a DEM (full-raster scan).
    /// </summary>
    [HttpPost("stockpile/detect-all", Name = nameof(ObjectsController) + "." + nameof(DetectAllStockpiles))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DetectAllStockpiles(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] StockpileBatchDetectionRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        var sensitivity = request.Sensitivity ?? 0.5f;
        if (sensitivity < 0f) sensitivity = 0f;
        if (sensitivity > 1f) sensitivity = 1f;

        var minAreaM2 = request.MinAreaM2 ?? 5.0;
        if (minAreaM2 < 0) throw new BadRequestException("minAreaM2 must be >= 0");

        var maxResults = request.MaxResults ?? 50;
        if (maxResults <= 0) throw new BadRequestException("maxResults must be > 0");
        if (maxResults > 500) maxResults = 500;

        var json = await _objectsManager.DetectAllStockpiles(orgSlug, dsSlug, request.Path,
            sensitivity, minAreaM2, maxResults);
        return Content(json, "application/json");
    }

    #endregion

    #region Contour Lines

    /// <summary>
    /// Generate contour lines (GeoJSON LineStrings with an `elev` property) from a DEM/DSM/DTM raster.
    /// </summary>
    /// <remarks>
    /// Either <c>interval</c> or <c>count</c> must be supplied. When both are set, <c>interval</c> wins.
    /// </remarks>
    [HttpPost("contours", Name = nameof(ObjectsController) + "." + nameof(GenerateContours))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateContours(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] ContourRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        if (!request.Interval.HasValue && !request.Count.HasValue)
            throw new BadRequestException("Either 'interval' or 'count' must be specified");

        if (request.Interval.HasValue && request.Interval.Value <= 0)
            throw new BadRequestException("'interval' must be > 0");

        if (request.Count.HasValue && request.Count.Value <= 0)
            throw new BadRequestException("'count' must be > 0");

        if (request.MinElev.HasValue && request.MaxElev.HasValue &&
            request.MinElev.Value >= request.MaxElev.Value)
            throw new BadRequestException("'minElev' must be less than 'maxElev'");

        if (request.SimplifyTolerance < 0)
            throw new BadRequestException("'simplifyTolerance' must be >= 0");

        var bandIndex = request.BandIndex > 0 ? request.BandIndex : 1;

        var json = await _objectsManager.GenerateContours(orgSlug, dsSlug, request.Path,
            request.Interval, request.Count,
            request.BaseOffset,
            request.MinElev, request.MaxElev,
            request.SimplifyTolerance,
            bandIndex);
        return Content(json, "application/json");
    }

    #endregion

    #region MaskBorders

    /// <summary>Check if a masked version of the file already exists.</summary>
    [HttpPost("mask-borders/check", Name = nameof(ObjectsController) + "." + nameof(CheckMaskBorders))]
    [ProducesResponseType(typeof(MaskBordersCheckResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckMaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request)
    {
        EnsureValidPath(request.Path);

        var result = await _objectsManager.CheckMaskedFileExists(orgSlug, dsSlug, request.Path);
        return Ok(result);
    }

    /// <summary>Start masking orthophoto borders to make them transparent.</summary>
    [HttpPost("mask-borders", Name = nameof(ObjectsController) + "." + nameof(MaskBorders))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request)
    {
        // Early path traversal check
        EnsureValidPath(request.Path);

        await _objectsManager.MaskBorders(orgSlug, dsSlug, request.Path, request.Near, request.White);
        return Ok(new { ok = true });
    }

    #endregion
}

