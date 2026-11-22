using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>The photos of a dataset that see a place.</summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix)]
[Produces("application/json")]
public class CoverageController : ControllerBaseEx
{
    // A geometry of the most positions it can have, with room for its coordinates and the rest of the body
    private const long MaxRequestBytes = 64 * 1024;

    private readonly CoverageService _coverage;

    public CoverageController(CoverageService coverage)
    {
        _coverage = coverage;
    }

    /// <summary>
    /// The images of the dataset that see a place, best view first.
    /// </summary>
    /// <remarks>
    /// The images whose footprint intersects a geometry: the photos that see a place.
    /// </remarks>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="request">The geometry (a GeoJSON Point, LineString or Polygon in WGS84, at most 10000 positions) and
    /// the most images to return (1 to 100, default 20).</param>
    /// <returns>For each image its <c>path</c>, its <c>score</c> (0 is the image whose middle is the geometry; lower is
    /// better), the <c>overlap</c> of a polygon with it (0 to 1; 1 for a point or a line) and the <c>pixel</c>
    /// <c>[x, y]</c> of the centre of the geometry in the image, or null when the camera parameters do not tell it.</returns>
    /// <response code="400">The geometry or the limit is not valid; the message names it.</response>
    /// <response code="403">The current user may not read the dataset.</response>
    /// <response code="404">There is no such dataset.</response>
    /// <response code="413">The body is larger than 64 KB.</response>
    [HttpPost("coverage", Name = nameof(CoverageController) + "." + nameof(FindCoverage))]
    [RequestSizeLimit(MaxRequestBytes)]
    [EnforceContentLength(MaxRequestBytes, "Coverage request")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(List<CoverageResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<List<CoverageResult>>> FindCoverage(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody] CoverageRequestDto? request, CancellationToken ct) =>
        await _coverage.FindAsync(orgSlug, dsSlug, request, ct);
}
