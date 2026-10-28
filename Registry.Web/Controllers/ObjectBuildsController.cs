#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using MimeMapping;
using Registry.Ports.DroneDB;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Objects;
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

    public ObjectBuildsController(BuildQueryService builds, IResponseCompressionProvider compression)
    {
        _builds = builds;
        _compression = compression;
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
        [FromRoute] string? path, CancellationToken ct)
    {
        var res = await _builds.GetBuildFileAsync(orgSlug, dsSlug, hash, path, ct: ct);

        return BuildFileResult(res);
    }

    // GET and HEAD send the same file result: the executor sends the headers (length, type, validators, ranges) of
    // both and the body of GET only. The type is the manifest's when libddb gives one, else the extension's. A
    // client may keep the file but must ask again before it uses it (If-None-Match, answered 304): a forced rebuild
    // rewrites the file under the same hash, so it is never immutable. The tag is weak for a type the response
    // compression encodes (a JSON tileset), strong for the binary files a viewer reads by ranges
    private PhysicalFileResult BuildFileResult(ResolvedBuildFile file)
    {
        Response.Headers.CacheControl = "private, no-cache";
        return FileTaggedPhysicalFile(file.Path, file.MediaType ?? MimeUtility.GetMimeMapping(file.Path), _compression);
    }

    /// <summary>
    /// Checks if a build file exists by hash and path: the headers of <c>GET</c> (length, type, validators, range
    /// support) without the body, or 404.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="hash">The build hash.</param>
    /// <param name="path">The path to the file within the build.</param>
    /// <returns>The headers of the file if it exists, NotFound otherwise.</returns>
    [BasicAuthentication]
    [HttpHead("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(CheckBuildFile))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckBuildFile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string hash,
        [FromRoute] string? path, CancellationToken ct)
    {
        var res = await _builds.FindBuildFileAsync(orgSlug, dsSlug, hash, path, ct: ct);

        return res == null ? NotFound() : BuildFileResult(res);
    }

    /// <summary>
    /// Downloads a pre-built visualization artifact (COG, COPC, or GPKG) for a given entry.
    /// The entry type determines which artifact is served, unless <paramref name="role"/> names one (for example the
    /// <c>gsplat.export</c> <c>.spz</c> of a splat, once its export task has made it), which is then served under the
    /// entry's name. Zero-copy streaming via PhysicalFileResult.
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

        return PhysicalFile(artifact.LocalPath, artifact.MediaType, artifact.FileName, enableRangeProcessing: true);
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
