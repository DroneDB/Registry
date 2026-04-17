using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.CompilerServices;
using MimeMapping;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Filters;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
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

    public ObjectsController(IObjectsManager datasetsManager, ILogger<ObjectsController> logger)
    {
        _objectsManager = datasetsManager;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the DroneDB database file for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The DDB file as a downloadable attachment.</returns>
    [HttpGet("ddb", Name = nameof(ObjectsController) + "." + nameof(GetDdb))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDdb(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Objects controller GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            var res = await _objectsManager.GetDdb(orgSlug, dsSlug);

            Response.StatusCode = 200;
            Response.ContentType = res.ContentType;

            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{res.Name}\"");

            await res.CopyToAsync(Response.Body);

            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — not an error
            _logger.LogDebug("Download cancelled by client: GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Objects controller GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug,
                dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Generates a thumbnail for a specific object.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path to the object.</param>
    /// <param name="size">Optional thumbnail size.</param>
    /// <returns>The thumbnail image file.</returns>
    [HttpGet("thumb", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnail))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateThumbnail(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string path,
        [FromQuery] int? size)
    {
        try
        {
            _logger.LogDebug("Objects controller GenerateThumbnail('{OrgSlug}', '{DsSlug}', '{Path}', '{Size}')",
                orgSlug, dsSlug, path, size);

            var res = await _objectsManager.GenerateThumbnailData(orgSlug, dsSlug, path, size);

            if (res == null)
            {
                _logger.LogWarning("Thumbnail generation returned null for '{OrgSlug}/{DsSlug}' path: '{Path}'",
                    orgSlug, dsSlug, path);
                return NotFound();
            }

            _logger.LogInformation("Successfully generated thumbnail for '{OrgSlug}/{DsSlug}' path: '{Path}', size: {DataSize} bytes",
                orgSlug, dsSlug, path, res.Data.Length);

            return File(res.Data, res.ContentType, res.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller GenerateThumbnail('{OrgSlug}', '{DsSlug}', '{Path}', '{Size}')",
                orgSlug, dsSlug, path, size);
            return ExceptionResult(new Exception("Cannot generate thumbnail"));
        }
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
    /// <returns>The tile image file.</returns>
    [HttpGet("tiles/{tz:int}/{tx:int}/{tyRaw}.{ext:regex(^(png|webp)$)}", Name = nameof(ObjectsController) + "." + nameof(GenerateTile))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateTile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] int tz,
        [FromRoute, Required] int tx,
        [FromRoute, Required] string tyRaw,
        [FromQuery] string path,
        [FromRoute, Required] string ext)
    {
        try
        {
            _logger.LogDebug(
                "Objects controller GenerateTile('{OrgSlug}', '{DsSlug}', '{Path}', '{Tz}', '{Tx}', '{TyRaw}', '{Ext}')",
                orgSlug, dsSlug, path, tz, tx, tyRaw, ext);

            var retina = tyRaw.EndsWith("@2x");

            if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
                throw new ArgumentException("Invalid input parameters (retina indicator should be '@2x')");

            var res = await _objectsManager.GenerateTileData(orgSlug, dsSlug, path, tz, tx, ty, retina);

            return File(res.Data, res.ContentType, res.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller GenerateTile('{OrgSlug}', '{DsSlug}', '{Path}', '{Tz}', '{Tx}', '{TyRaw}')",
                orgSlug, dsSlug, path, tz, tx, tyRaw);
            return ExceptionResult(ex);
        }
    }

    #region Downloads

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

            Response.StatusCode = 200;
            Response.ContentType = res.ContentType;

            Response.Headers.Append("Content-Disposition",
                isInline ? "inline" : $"attachment; filename=\"{res.Name}\"");

            await res.CopyToAsync(Response.Body);

            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — not an error
            _logger.LogDebug(
                "Download cancelled by client: Download('{OrgSlug}', '{DsSlug}', '{Paths}')",
                orgSlug, dsSlug, paths != null ? string.Join("; ", paths) : string.Empty);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller Download('{OrgSlug}', '{DsSlug}', '{Paths}', '{IsInline}')",
                orgSlug, dsSlug, paths != null ? string.Join("; ", paths) : string.Empty, isInline);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Downloads one or more objects from a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="pathsRaw">Comma-separated list of paths to download.</param>
    /// <param name="isInlineRaw">If 1, display inline instead of as attachment.</param>
    /// <returns>The file(s) as a downloadable stream.</returns>
    [HttpGet("download", Name = nameof(ObjectsController) + "." + nameof(Download))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Download(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery(Name = "path")] string pathsRaw,
        [FromQuery(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        var paths = pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);
        var isInline = isInlineRaw == 1;

        _logger.LogDebug("Objects controller Download('{OrgSlug}', '{DsSlug}', '{PathsRaw}', '{IsInlineRaw}')",
            orgSlug, dsSlug, pathsRaw, isInlineRaw);

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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<IActionResult> DownloadPost(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "path")] string pathsRaw,
        [FromForm(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        var paths = pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);
        var isInline = isInlineRaw == 1;

        _logger.LogDebug(
            "Objects controller DownloadPost('{OrgSlug}', '{DsSlug}', '{PathsRaw}', '{IsInlineRaw}')", orgSlug,
            dsSlug, pathsRaw, isInlineRaw);

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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadExact(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute] string path,
        [FromQuery(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        var isInline = isInlineRaw == 1;

        _logger.LogDebug("Objects controller DownloadExact('{OrgSlug}', '{DsSlug}', '{Path}', '{IsInlineRaw}')",
            orgSlug, dsSlug, path, isInlineRaw);

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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string path)
    {
        try
        {
            _logger.LogDebug("Objects controller Get('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);

            var res = await _objectsManager.Get(orgSlug, dsSlug, path);
            return PhysicalFile(res.PhysicalPath, res.ContentType, res.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Objects controller Get('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug,
                dsSlug, path);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string path,
        [FromQuery] EntryType? type = null)
    {
        try
        {
            _logger.LogDebug("Objects controller GetInfo('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                dsSlug, path, type);

            var res = await _objectsManager.List(orgSlug, dsSlug, path, false, type);
            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller GetInfo('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                dsSlug, path, type);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInfoEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string path,
        [FromForm] EntryType? type = null)
    {
        try
        {
            _logger.LogDebug("Objects controller GetInfoEx('{OrgSlug}', '{DsSlug}', '{Path}', {Type})", orgSlug,
                dsSlug, path, type);

            var res = await _objectsManager.List(orgSlug, dsSlug, path, false, type);
            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller GetInfoEx('{OrgSlug}', '{DsSlug}', '{Path}, {Type})", orgSlug,
                dsSlug, path, type);
            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string query,
        [FromForm] string path,
        [FromForm] bool recursive = true,
        [FromForm] EntryType? type = null)
    {
        try
        {
            _logger.LogDebug("Objects controller Search('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                dsSlug, path, type);

            var res = await _objectsManager.Search(orgSlug, dsSlug, query, path, recursive, type);
            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller Search('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                dsSlug, path, type);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Creates or uploads a new object to a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The path where to create the object.</param>
    /// <param name="file">Optional file to upload.</param>
    /// <returns>The created entry.</returns>
    [HttpPost(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + "." + nameof(Post))]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [DisableRequestSizeLimit]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Post(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string path,
        IFormFile file = null)
    {
        try
        {
            _logger.LogDebug("Objects controller Post('{OrgSlug}', '{DsSlug}', '{Path}', '{file?.FileName}')",
                orgSlug, dsSlug, path, file?.FileName);

            EntryDto newObj;

            if (file == null)
            {
                newObj = await _objectsManager.AddNew(orgSlug, dsSlug, path);
            }
            else
            {
                await using var stream = file.OpenReadStream();
                newObj = await _objectsManager.AddNew(orgSlug, dsSlug, path, stream);
            }

            return CreatedAtRoute(nameof(ObjectsController) + "." + nameof(GetInfo), new
            {
                orgSlug,
                dsSlug,
                path = newObj.Path
            }, newObj);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller Post('{OrgSlug}', '{DsSlug}', '{Path}', '{file?.FileName}')",
                orgSlug, dsSlug, path, file?.FileName);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string path)
    {
        try
        {
            _logger.LogDebug("Objects controller Delete('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);

            await _objectsManager.Delete(orgSlug, dsSlug, path);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Objects controller Delete('{OrgSlug}', '{DsSlug}', '{Path}')",
                orgSlug, dsSlug, path);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteBatch(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string[] paths)
    {
        try
        {
            _logger.LogDebug("Objects controller DeleteBatch('{OrgSlug}', '{DsSlug}', {Count} paths)",
                orgSlug, dsSlug, paths?.Length ?? 0);

            if (paths == null || paths.Length == 0)
                return BadRequest(new ErrorResponse("No paths provided"));

            var response = await _objectsManager.DeleteBatch(orgSlug, dsSlug, paths);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Objects controller DeleteBatch('{OrgSlug}', '{DsSlug}')",
                orgSlug, dsSlug);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(Entry), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Move(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string source,
        [FromForm] string dest)
    {
        try
        {
            _logger.LogDebug("Objects controller Move('{OrgSlug}', '{DsSlug}', '{Source}', '{Dest}')", orgSlug,
                dsSlug, source, dest);

            var entry = await _objectsManager.Move(orgSlug, dsSlug, source, dest);
            return Ok(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller Move('{OrgSlug}', '{DsSlug}', '{Source}', '{Dest}')", orgSlug,
                dsSlug, source, dest);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Transfer(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string sourcePath,
        [FromForm, Required] string destOrgSlug,
        [FromForm, Required] string destDsSlug,
        [FromForm] string destPath = null,
        [FromForm] bool overwrite = false)
    {
        try
        {
            _logger.LogDebug("Objects controller Transfer('{SourceOrgSlug}', '{SourceDsSlug}', '{SourcePath}', '{DestOrgSlug}', '{DestDsSlug}', '{DestPath}', '{Overwrite}')", orgSlug,
                dsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite);

            await _objectsManager.Transfer(orgSlug, dsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller Transfer('{SourceOrgSlug}', '{SourceDsSlug}', '{SourcePath}', '{DestOrgSlug}', '{DestDsSlug}', '{DestPath}', '{Overwrite}')", orgSlug,
                dsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite);

            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Build(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string path,
        [FromForm] bool force = false)
    {
        try
        {
            _logger.LogDebug("Objects controller Build('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);

            await _objectsManager.Build(orgSlug, dsSlug, path, force);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Objects controller Build('{OrgSlug}', '{DsSlug}', '{Path}')",
                orgSlug, dsSlug, path);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets a file from a build by hash and path.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <returns>The build file.</returns>
    [ServiceFilter(typeof(BasicAuthFilter))]
    [HttpGet("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(GetBuildFile))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string path)
    {
        try
        {
            _logger.LogDebug("Objects controller GetBuildFile('{OrgSlug}', '{DsSlug}', '{Hash}', '{Path}')", orgSlug, dsSlug,
                hash, path);

            var res = await _objectsManager.GetBuildFile(orgSlug, dsSlug, hash, path);

            return PhysicalFile(res, MimeUtility.GetMimeMapping(res), true);
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

    /// <summary>
    /// Checks if a build file exists by hash and path.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <returns>Ok if file exists, NotFound otherwise.</returns>
    [ServiceFilter(typeof(BasicAuthFilter))]
    [HttpHead("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(CheckBuildFile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string path)
    {
        try
        {
            _logger.LogDebug("Objects controller CheckBuildFile('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug,
                dsSlug, path);

            var res = await _objectsManager.CheckBuildFile(orgSlug, dsSlug, hash, path);

            return res ? Ok() : NotFound();
        }
        catch (UnauthorizedException ex)
        {
            BasicAuthFilter.SendBasicAuthRequest(Response);
            return ExceptionResult(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller CheckBuildFile('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug,
                dsSlug, path);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets a paginated list of build jobs for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A list of build jobs.</returns>
    [HttpGet("builds", Name = nameof(ObjectsController) + "." + nameof(GetBuilds))]
    [ProducesResponseType(typeof(IEnumerable<BuildJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            _logger.LogDebug("Objects controller GetBuilds('{OrgSlug}', '{DsSlug}', page: {Page}, pageSize: {PageSize})",
                orgSlug, dsSlug, page, pageSize);

            var builds = await _objectsManager.GetBuilds(orgSlug, dsSlug, page, pageSize);

            return Ok(builds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller GetBuilds('{OrgSlug}', '{DsSlug}', page: {Page}, pageSize: {PageSize})",
                orgSlug, dsSlug, page, pageSize);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Clears all completed build jobs for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The number of deleted build jobs.</returns>
    [HttpPost("builds/clear", Name = nameof(ObjectsController) + "." + nameof(ClearCompletedBuilds))]
    [ProducesResponseType(typeof(ClearCompletedBuildsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearCompletedBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Objects controller ClearCompletedBuilds('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            var deletedCount = await _objectsManager.ClearCompletedBuilds(orgSlug, dsSlug);

            return Ok(new ClearCompletedBuildsResponse { DeletedCount = deletedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception in Objects controller ClearCompletedBuilds('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            return ExceptionResult(ex);
        }
    }

    #region Multispectral

    /// <summary>Get raster info (bands, sensor profile, presets) for a file.</summary>
    [HttpGet("raster-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path)
    {
        try
        {
            var json = await _objectsManager.GetRasterInfo(orgSlug, dsSlug, path);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetRasterInfo('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Get raster statistics/histogram for a band or formula.</summary>
    [HttpGet("raster-metadata", Name = nameof(ObjectsController) + "." + nameof(GetRasterMetadata))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterMetadata(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter)
    {
        try
        {
            var json = await _objectsManager.GetRasterMetadata(orgSlug, dsSlug, path, formula, bandFilter);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetRasterMetadata('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Generate thumbnail with extended visualization params.</summary>
    [HttpGet("thumb-ex", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnailEx))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
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
        try
        {
            var res = await _objectsManager.GenerateThumbnailDataEx(orgSlug, dsSlug, path, size,
                preset, bands, formula, bandFilter, colormap, rescale);

            if (res == null) return NotFound();
            return File(res.Data, res.ContentType, res.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GenerateThumbnailEx('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(new Exception("Cannot generate thumbnail"));
        }
    }

    /// <summary>Generate tile with extended visualization params.</summary>
    [HttpGet("tiles-ex/{tz:int}/{tx:int}/{tyRaw}.{ext:regex(^(png|webp)$)}", Name = nameof(ObjectsController) + "." + nameof(GenerateTileEx))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
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
        try
        {
            var retina = tyRaw.EndsWith("@2x");
            if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
                throw new ArgumentException("Invalid input parameters (retina indicator should be '@2x')");

            var res = await _objectsManager.GenerateTileDataEx(orgSlug, dsSlug, path, tz, tx, ty, retina,
                preset, bands, formula, bandFilter, colormap, rescale);

            return File(res.Data, res.ContentType, res.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GenerateTileEx('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Export raster with visualization params applied as GeoTIFF.</summary>
    [HttpGet("export", Name = nameof(ObjectsController) + "." + nameof(ExportRaster))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExportRaster(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery] string? format = "geotiff",
        [FromQuery] string? preset = null,
        [FromQuery] string? bands = null,
        [FromQuery] string? formula = null,
        [FromQuery] string? bandFilter = null,
        [FromQuery] string? colormap = null,
        [FromQuery] string? rescale = null)
    {
        try
        {
            var res = await _objectsManager.ExportRaster(orgSlug, dsSlug, path,
                preset, bands, formula, bandFilter, colormap, rescale);

            return File(res.Data, res.ContentType, res.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in ExportRaster('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Validate merge-multispectral inputs.</summary>
    [HttpPost("merge-multispectral/validate", Name = nameof(ObjectsController) + "." + nameof(ValidateMergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateMergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        try
        {
            var json = await _objectsManager.ValidateMergeMultispectral(orgSlug, dsSlug, request.Paths);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in ValidateMergeMultispectral('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Preview merge-multispectral result.</summary>
    [HttpPost("merge-multispectral/preview", Name = nameof(ObjectsController) + "." + nameof(PreviewMergeMultispectral))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewMergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        try
        {
            var data = await _objectsManager.PreviewMergeMultispectral(orgSlug, dsSlug, request.Paths,
                request.PreviewBands, request.ThumbSize ?? 512);
            return File(data, "image/webp", "preview.webp");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in PreviewMergeMultispectral('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Merge single-band rasters into a multi-band COG.</summary>
    [HttpPost("merge-multispectral", Name = nameof(ObjectsController) + "." + nameof(MergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OutputPath))
                return BadRequest(new ErrorResponse("outputPath is required"));

            // Early path traversal check at controller level
            if (Path.IsPathRooted(request.OutputPath) ||
                request.OutputPath.Contains("..") ||
                request.OutputPath.Contains('\\'))
                return BadRequest(new ErrorResponse("Invalid output path"));

            await _objectsManager.MergeMultispectral(orgSlug, dsSlug, request.Paths, request.OutputPath);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in MergeMultispectral('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    #endregion

    #region Thermal

    /// <summary>Get thermal info including calibration, temperature range, and dimensions.</summary>
    [HttpGet("thermal-info", Name = nameof(ObjectsController) + "." + nameof(GetThermalInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetThermalInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path)
    {
        try
        {
            var json = await _objectsManager.GetThermalInfo(orgSlug, dsSlug, path);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetThermalInfo('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Get temperature at a specific pixel location.</summary>
    [HttpGet("thermal-point", Name = nameof(ObjectsController) + "." + nameof(GetThermalPoint))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetThermalPoint(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery, Required] int x,
        [FromQuery, Required] int y)
    {
        try
        {
            var json = await _objectsManager.GetThermalPoint(orgSlug, dsSlug, path, x, y);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetThermalPoint('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Get temperature statistics for a rectangular area.</summary>
    [HttpGet("thermal-area-stats", Name = nameof(ObjectsController) + "." + nameof(GetThermalAreaStats))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetThermalAreaStats(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path,
        [FromQuery, Required] int x0,
        [FromQuery, Required] int y0,
        [FromQuery, Required] int x1,
        [FromQuery, Required] int y1)
    {
        try
        {
            var json = await _objectsManager.GetThermalAreaStats(orgSlug, dsSlug, path, x0, y0, x1, y1);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in GetThermalAreaStats('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);
            return ExceptionResult(ex);
        }
    }

    #endregion

    #region MaskBorders

    /// <summary>Check if a masked version of the file already exists.</summary>
    [HttpPost("mask-borders/check", Name = nameof(ObjectsController) + "." + nameof(CheckMaskBorders))]
    [ProducesResponseType(typeof(MaskBordersCheckResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckMaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request)
    {
        try
        {
            _logger.LogDebug("Objects controller CheckMaskBorders('{OrgSlug}', '{DsSlug}', '{Path}')",
                orgSlug, dsSlug, request.Path);

            var result = await _objectsManager.CheckMaskedFileExists(orgSlug, dsSlug, request.Path);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in CheckMaskBorders('{OrgSlug}', '{DsSlug}', '{Path}')",
                orgSlug, dsSlug, request.Path);
            return ExceptionResult(ex);
        }
    }

    /// <summary>Start masking orthophoto borders to make them transparent.</summary>
    [HttpPost("mask-borders", Name = nameof(ObjectsController) + "." + nameof(MaskBorders))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request)
    {
        try
        {
            _logger.LogDebug("Objects controller MaskBorders('{OrgSlug}', '{DsSlug}', '{Path}')",
                orgSlug, dsSlug, request.Path);

            // Early path traversal check
            if (Path.IsPathRooted(request.Path) ||
                request.Path.Contains("..") ||
                request.Path.Contains('\\'))
                return BadRequest(new ErrorResponse("Invalid path"));

            await _objectsManager.MaskBorders(orgSlug, dsSlug, request.Path, request.Near, request.White);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in MaskBorders('{OrgSlug}', '{DsSlug}', '{Path}')",
                orgSlug, dsSlug, request.Path);
            return ExceptionResult(ex);
        }
    }

    #endregion


}