using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Features;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Processing;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// The platform features the Hub adapts to (RQ-12, out of <see cref="SystemController"/>). Anonymous.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class FeaturesController : ControllerBaseEx
{
    private readonly IFeaturesBuilder _features;

    public FeaturesController(IFeaturesBuilder features) => _features = features;

    /// <summary>
    /// Gets the status of all platform feature flags.
    /// </summary>
    /// <returns>An object with the status of each feature.</returns>
    [AllowAnonymous]
    [HttpGet("features", Name = SystemRoutes.Prefix + nameof(GetFeatures))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FeaturesDto>> GetFeatures(CancellationToken ct) => await _features.BuildAsync(ct: ct);
}
