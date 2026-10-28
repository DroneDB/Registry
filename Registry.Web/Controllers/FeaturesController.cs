using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Features;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The platform features the Hub adapts to (out of <see cref="SystemController"/>). Anonymous.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class FeaturesController : ControllerBaseEx
{
    private readonly FeaturesBuilder _features;

    public FeaturesController(FeaturesBuilder features) => _features = features;

    /// <summary>
    /// Gets the status of all platform feature flags.
    /// </summary>
    /// <returns>An object with the status of each feature.</returns>
    [AllowAnonymous]
    [HttpGet("features", Name = SystemRoutes.Prefix + nameof(GetFeatures))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FeaturesDto>> GetFeatures(CancellationToken ct) => await _features.BuildAsync(ct: ct);
}
