using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// What the update check knows and what an administrator tells it (out of <see cref="SystemController"/>). Same
/// routes, same route names (<see cref="SystemRoutes"/>).
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class UpdateCheckController : ControllerBaseEx
{
    private readonly IUpdateCheckService _updateCheck;
    private readonly UpdateCheckSettingsUpdater _updateCheckSettings;

    public UpdateCheckController(IUpdateCheckService updateCheck, UpdateCheckSettingsUpdater updateCheckSettings)
    {
        _updateCheck = updateCheck;
        _updateCheckSettings = updateCheckSettings;
    }

    /// <summary>
    /// What the update check knows: the installed and the latest version, whether an update is available and
    /// whether it carries security fixes, the merged release notes (Markdown from the update check service: render it
    /// sanitized), the link to request a licence, and what the administrator declared. The version fields are null until
    /// a check succeeded.
    /// </summary>
    /// <returns>The update information.</returns>
    [HttpGet("update-info", Name = SystemRoutes.Prefix + nameof(GetUpdateInfo))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public ActionResult<UpdateInfoDto> GetUpdateInfo()
    {
        return _updateCheck.GetUpdateInfo();
    }

    /// <summary>
    /// Saves how the deployment is used and, with the administrator's consent, the address to send security and release
    /// news to. A change queues one update check (at most one every 10 minutes, none while the check is disabled), so
    /// the declaration reaches the update check service the same day.
    /// </summary>
    /// <param name="request">The declaration, the address and the consent.</param>
    /// <returns>The update information after the change.</returns>
    /// <response code="400">The declaration is unknown, the address is not valid, or an address came without consent.</response>
    [HttpPut("update-check/settings", Name = SystemRoutes.Prefix + nameof(PutUpdateCheckSettings))]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public ActionResult<UpdateInfoDto> PutUpdateCheckSettings([FromBody, Required] UpdateCheckSettingsDto request)
    {
        _updateCheckSettings.Apply(request);
        return _updateCheck.GetUpdateInfo();
    }
}
