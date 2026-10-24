#nullable enable
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// Admin-only dashboard listing tasks across all users and datasets.
/// Lives under the system radix (<c>/sys/tasks</c>). Per-task actions (log, result,
/// cancel, retry) reuse the existing per-dataset endpoints in <see cref="TasksController"/>,
/// which already authorize admins via dataset ownership.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix + "/tasks")]
[Produces("application/json")]
public class AdminTasksController : ControllerBaseEx
{
    private readonly AdminTasksManager _admin;

    public AdminTasksController(AdminTasksManager admin)
    {
        _admin = admin;
    }

    // ---- GET /sys/tasks ---------------------------------------------------

    [HttpGet(Name = nameof(AdminTasksController) + "." + nameof(List))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminTaskListDto>> List(
        [FromQuery] string? toolId, [FromQuery] string? state, [FromQuery] string? userId,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        return await _admin.ListAsync(toolId, state, userId, skip, take, ct);
    }

    // ---- POST /sys/tasks/{id}/delete ------------------------------------

    [HttpPost("{id}/delete", Name = nameof(AdminTasksController) + "." + nameof(Delete))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DeletedDto>> Delete(
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var deleted = await _admin.DeleteTaskAsync(id, ct);
        if (!deleted)
            throw new NotFoundException("Task not found or cannot be deleted (only concluded tasks can be removed from history)");

        return new DeletedDto(deleted);
    }
}
