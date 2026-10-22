#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MimeMapping;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The builds of a dataset's entries: starting one, the files and the downloadable artifact of a build (as
/// libddb lists them, XI-04), the builds waiting for a dependency, and the record of finished build jobs.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug)]
[Produces("application/json")]
public class ObjectBuildsController : ControllerBaseEx
{
    private readonly IBuildQueryService _builds;

    public ObjectBuildsController(IBuildQueryService builds)
    {
        _builds = builds;
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
        [FromForm] bool force = false)
    {
        await _builds.BuildAsync(orgSlug, dsSlug, path, force);
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
        [FromRoute] string? path)
    {
        var res = await _builds.GetBuildFileAsync(orgSlug, dsSlug, hash, path);

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
        [FromRoute] string? path)
    {
        var res = await _builds.CheckBuildFileAsync(orgSlug, dsSlug, hash, path);

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
        [FromQuery] DatasetPath? path)
    {
        var artifact = await _builds.GetBuildArtifactAsync(orgSlug, dsSlug, path);

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
    [ProducesResponseType(typeof(IEnumerable<PendingBuildInfoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingBuilds(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        var pending = await _builds.GetPendingBuildsAsync(orgSlug, dsSlug);

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
        var deletedCount = await _builds.ClearCompletedBuildsAsync(orgSlug, dsSlug);

        return Ok(new ClearCompletedBuildsResponse { DeletedCount = deletedCount });
    }
}
