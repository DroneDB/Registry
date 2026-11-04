using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Datasets;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The places of the datasets of an organization, for a map: <c>/orgs/{org}/locations</c>. A sibling of the
/// dataset routes (<c>/orgs/{org}/ds</c>), so no dataset slug can collide with it.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationPrefix + "/locations")]
[Produces("application/json")]
public class DatasetLocationsController : ControllerBaseEx
{
    private readonly DatasetLocationQueries _locations;

    public DatasetLocationsController(DatasetLocationQueries locations)
    {
        _locations = locations;
    }

    /// <summary>
    /// The location of each dataset of the organization the caller can read (the rule of the dataset list), without
    /// thumbnails. A dataset without a location has a null <c>bbox</c>. Datasets that are unlisted are left out for a
    /// caller who is not an owner, administrator or member of the organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The locations, by dataset slug.</returns>
    [HttpGet(Name = nameof(GetDatasetLocations))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<DatasetLocationDto>>> GetDatasetLocations(
        [FromRoute, Required] string orgSlug, CancellationToken ct)
    {
        return Answer<IEnumerable<DatasetLocationDto>>(await _locations.List(orgSlug, ct));
    }
}
