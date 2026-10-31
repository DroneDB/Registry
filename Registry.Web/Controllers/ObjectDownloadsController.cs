using System;
using System.Linq;
using System.Threading;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Downloads of a dataset's entries: one file (served from disk, with ranges), a ZIP archive of a selection or
/// of the whole dataset, and the dataset's index (<c>.ddb</c>); archives are written while they are sent.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix)]
[Produces("application/json")]
public class ObjectDownloadsController : ControllerBaseEx
{
    private readonly IEntryQueryService _queries;
    private readonly ILogger<ObjectDownloadsController> _logger;

    public ObjectDownloadsController(IEntryQueryService queries, ILogger<ObjectDownloadsController> logger)
    {
        _queries = queries;
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
            var res = await _queries.GetDdbAsync(orgSlug, dsSlug, cancellationToken);
            return new StreamedDownloadResult(res, inline: false, _logger);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - not an error
            _logger.LogDebug("Download cancelled by client: GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return new EmptyResult();
        }
    }

    #region Downloads

    private async Task<IActionResult> InternalDownload(string orgSlug, string dsSlug, string[]? paths, bool isInline,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // If only one file is requested, we can leverage the local file system
            if (paths?.Length == 1 &&
                await _queries.GetEntryTypeAsync(orgSlug, dsSlug, paths[0], cancellationToken) != EntryType.Directory)
            {
                var r = await _queries.GetAsync(orgSlug, dsSlug, paths[0], cancellationToken);
                return UserEntryFile(r.PhysicalPath, r.Name, isInline);
            }

            // Anything else is an archive of a selection, a folder or the whole dataset: always an attachment
            var res = await _queries.DownloadAsync(orgSlug, dsSlug, paths, cancellationToken);
            return new StreamedDownloadResult(res, inline: false, _logger);
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
        [FromQuery(Name = "path")] string? pathsRaw,
        [FromQuery(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        // NOTE: read the raw query here instead of trusting the [FromQuery] string? binding:
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
        [FromForm(Name = "path")] string? pathsRaw,
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
        [FromRoute] string? path,
        [FromQuery(Name = "inline")] int? isInlineRaw,
        CancellationToken cancellationToken)
    {
        var isInline = isInlineRaw == 1;

        // No path (the route's catch-all is empty) is the empty path: "Path should not be null"
        return await InternalDownload(orgSlug, dsSlug, [path ?? string.Empty], isInline, cancellationToken);
    }

    #endregion
}
