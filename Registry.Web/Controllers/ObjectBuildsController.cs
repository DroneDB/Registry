using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The builds of a dataset's entries: starting one, the files and the downloadable artifact of a build (as
/// libddb lists them), the builds waiting for a dependency, and the record of finished build jobs.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix)]
[Produces("application/json")]
public class ObjectBuildsController : ControllerBaseEx
{
    private readonly BuildQueryService _builds;
    private readonly IResponseCompressionProvider _compression;
    private readonly IZipArchiveBuilder _zip;
    private readonly ILogger<ObjectBuildsController> _logger;

    public ObjectBuildsController(BuildQueryService builds, IResponseCompressionProvider compression,
        IZipArchiveBuilder zip, ILogger<ObjectBuildsController> logger)
    {
        _builds = builds;
        _compression = compression;
        _zip = zip;
        _logger = logger;
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
        [FromForm] string? path,
        [FromForm] bool force = false, CancellationToken ct = default)
    {
        await _builds.BuildAsync(orgSlug, dsSlug, path, force, ct: ct);
        return Ok();
    }

    /// <summary>
    /// Gets a file from a build by hash and path. The URLs of the artifacts of an entry name the version of the
    /// artifact (<c>build/{hash}@{version}/{path}</c>): while it is the current one, the file may be kept without
    /// revalidation.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <param name="version">The version of the artifact the URL names, if any.</param>
    /// <returns>The build file.</returns>
    [BasicAuthentication]
    [HttpGet(RoutesHelper.BuildFile, Name = nameof(ObjectsController) + "." + nameof(GetBuildFile))]
    [HttpGet(RoutesHelper.VersionedBuildFile, Name = nameof(ObjectsController) + "." + nameof(GetBuildFile) + "Versioned")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string? path,
        [FromRoute] string? version, CancellationToken ct)
    {
        var res = await _builds.GetBuildFileAsync(orgSlug, dsSlug, hash, path, ct: ct);

        return BuildFileResult(res, version);
    }

    // GET and HEAD send the same file result: the executor sends the headers (length, type, validators, ranges) of
    // both and the body of GET only. The type is the manifest's when libddb gives one and the policy for user content
    // holds it, else the extension's (a type the policy does not know is an attachment of octet-stream, and every
    // response is sandboxed, because the files of a build are the user's own bytes). A URL
    // that names the current version of the file's artifact identifies its bytes: the browser keeps them without
    // asking (immutable, for the bounded VersionedMaxAge), every range of a file read by ranges included. Any other
    // URL (no version, or one a rebuild replaced) may change under the same hash, so the client must ask again
    // before it uses the file (If-None-Match, answered 304). The tag is weak for a type the response compression
    // encodes (a JSON tileset), strong for the binary files a viewer reads by ranges
    private PhysicalFileResult BuildFileResult(ResolvedBuildFile file, string? requestedVersion)
    {
        var served = UserContentPolicy.ForBuildFile(file.Path, file.MediaType);
        Response.Headers.CacheControl = HashedContentCachePolicy.CacheControlFor(file.Version, requestedVersion);
        Response.Headers.ContentSecurityPolicy = served.ContentSecurityPolicy;
        return FileTaggedPhysicalFile(file.Path, served.MediaType, _compression,
            served.Inline ? null : System.IO.Path.GetFileName(file.Path));
    }

    /// <summary>
    /// Checks if a build file exists by hash and path: the headers of <c>GET</c> (length, type, validators, range
    /// support) without the body, or 404.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <param name="version">The version of the artifact the URL names, if any.</param>
    /// <returns>The headers of the file if it exists, NotFound otherwise.</returns>
    [BasicAuthentication]
    [HttpHead(RoutesHelper.BuildFile, Name = nameof(ObjectsController) + "." + nameof(CheckBuildFile))]
    [HttpHead(RoutesHelper.VersionedBuildFile, Name = nameof(ObjectsController) + "." + nameof(CheckBuildFile) + "Versioned")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string? path,
        [FromRoute] string? version, CancellationToken ct)
    {
        var res = await _builds.FindBuildFileAsync(orgSlug, dsSlug, hash, path, ct: ct);

        return res == null ? NotFound() : BuildFileResult(res, version);
    }

    /// <summary>
    /// Downloads a built artifact of an entry (COG, COPC, GPKG, the 3D Tiles of a model...), named after the entry.
    /// The entry type determines which artifact is served, unless <paramref name="role"/> names one (for example the
    /// <c>gsplat.export</c> <c>.spz</c> of a splat, once its export task has made it). A file is streamed with range
    /// support; a directory (3D Tiles) is sent as a ZIP archive of what is in it, written while it is read.
    /// </summary>
    [HttpGet("build-artifact", Name = nameof(ObjectsController) + "." + nameof(DownloadBuildArtifact))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadBuildArtifact(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] DatasetPath? path, [FromQuery] string? role, CancellationToken ct)
    {
        var artifact = await _builds.GetBuildArtifactAsync(orgSlug, dsSlug, path, role, ct);

        return artifact.IsArchive
            ? new StreamedDownloadResult(new FolderArchiveDownload(artifact.FileName, artifact.LocalPath, _zip),
                inline: false, _logger)
            : PhysicalFile(artifact.LocalPath, artifact.MediaType, artifact.FileName, enableRangeProcessing: true);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<PendingBuildInfoDto>>> GetPendingBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        var pending = await _builds.GetPendingBuildsAsync(orgSlug, dsSlug, ct: ct);

        return Answer(pending);
    }

    /// <summary>
    /// Clears all completed build jobs for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The number of deleted build jobs.</returns>
    [HttpPost("builds/clear", Name = nameof(ObjectsController) + "." + nameof(ClearCompletedBuilds))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClearCompletedBuildsResponse>> ClearCompletedBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        var deletedCount = await _builds.ClearCompletedBuildsAsync(orgSlug, dsSlug, ct: ct);

        return new ClearCompletedBuildsResponse { DeletedCount = deletedCount };
    }
}
