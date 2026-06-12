#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Admin-only dashboard listing tasks across all users and datasets (spec §B.1.4).
/// Lives under the system radix (<c>/sys/tasks</c>). Per-task actions (log, result,
/// cancel, retry) reuse the existing per-dataset endpoints in <see cref="TasksController"/>,
/// which already authorize admins via dataset ownership (spec §B.2).
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix + "/tasks")]
[Produces("application/json")]
public class AdminTasksController : ControllerBaseEx
{
    private readonly IAdminTasksManager _admin;

    public AdminTasksController(IAdminTasksManager admin)
    {
        _admin = admin;
    }

    // ---- GET /sys/tasks ---------------------------------------------------

    [HttpGet(Name = nameof(AdminTasksController) + "." + nameof(List))]
    [ProducesResponseType(typeof(AdminTaskListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? toolId, [FromQuery] string? state, [FromQuery] string? userId,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _admin.ListAsync(toolId, state, userId, skip, take, ct));
        }
        catch (Exception ex)
        {
            return ExceptionResult(ex);
        }
    }
}
