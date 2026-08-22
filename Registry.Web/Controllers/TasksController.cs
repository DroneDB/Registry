#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Processing Platform task substrate REST surface. All routes live
/// under <c>/orgs/{org}/ds/{ds}/tasks</c>. Authorization combines dataset access
/// with per-task ownership.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug + "/tasks")]
[Produces("application/json")]
public class TasksController : ControllerBaseEx
{
    private readonly ITasksManager _tasksManager;
    private readonly ILogger<TasksController> _logger;

    public TasksController(ITasksManager tasksManager, ILogger<TasksController> logger)
    {
        _tasksManager = tasksManager;
        _logger = logger;
    }

    // ---- GET /tasks/tools -------------------------------------------------

    [HttpGet("tools", Name = nameof(TasksController) + "." + nameof(GetTools))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTools([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug)
        => Ok(await _tasksManager.GetToolsAsync(orgSlug, dsSlug));

    // ---- POST /tasks ------------------------------------------------------

    [HttpPost(Name = nameof(TasksController) + "." + nameof(Submit))]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromBody] SubmitTaskRequestDto body, CancellationToken ct)
    {
        try
        {
            var response = await _tasksManager.SubmitAsync(orgSlug, dsSlug, body, ct);

            // Dedup hit returns 200; fresh enqueue returns 202.
            return response.Deduplicated ? Ok(response) : Accepted(response.StatusUrl, response);
        }
        catch (Exception ex) when (ex is not (BadRequestException or ForbiddenException or UnauthorizedException))
        {
            // Managed client errors above replaced early returns that never logged; everything
            // else is a genuine failure and keeps the tool context in the incident log.
            _logger.LogError(ex, "Task submit failed for tool '{ToolId}' on {OrgSlug}/{DsSlug}", body?.ToolId, orgSlug,
                dsSlug);
            throw;
        }
    }

    // ---- GET /tasks -------------------------------------------------------

    [HttpGet(Name = nameof(TasksController) + "." + nameof(List))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromQuery] string? toolId, [FromQuery] string? state,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await _tasksManager.ListAsync(orgSlug, dsSlug, toolId, state, skip, take, ct));

    // ---- POST /tasks/clear ------------------------------------------------

    [HttpPost("clear", Name = nameof(TasksController) + "." + nameof(Clear))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clear(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromQuery] string? toolId, CancellationToken ct)
        => Ok(new { cleared = await _tasksManager.ClearAsync(orgSlug, dsSlug, toolId, ct) });

    // ---- GET /tasks/{id} --------------------------------------------------

    [HttpGet("{id}", Name = nameof(TasksController) + "." + nameof(GetStatus))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
        => Ok(await _tasksManager.GetStatusAsync(orgSlug, dsSlug, id, ct));

    // ---- GET /tasks/{id}/log ----------------------------------------------

    [HttpGet("{id}/log", Name = nameof(TasksController) + "." + nameof(GetLog))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLog(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, [FromQuery] long since = 0, CancellationToken ct = default)
        => Ok(await _tasksManager.GetLogAsync(orgSlug, dsSlug, id, since, ct));

    // ---- GET /tasks/{id}/result -------------------------------------------

    [HttpGet("{id}/result", Name = nameof(TasksController) + "." + nameof(GetResult))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetResult(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var artifact = await _tasksManager.GetResultAsync(orgSlug, dsSlug, id, ct);

        if (artifact.ETag is not null)
            Response.Headers.ETag = artifact.ETag;

        var stream = System.IO.File.OpenRead(artifact.FilePath);
        return File(stream, artifact.ContentType, artifact.FileName, enableRangeProcessing: true);
    }

    // ---- DELETE /tasks/{id} -----------------------------------------------

    [HttpDelete("{id}", Name = nameof(TasksController) + "." + nameof(Cancel))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
        => Ok(new { deleted = await _tasksManager.CancelAsync(orgSlug, dsSlug, id, ct) });

    // ---- POST /tasks/{id}/retry -------------------------------------------

    [HttpPost("{id}/retry", Name = nameof(TasksController) + "." + nameof(Retry))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
        => Ok(new { requeued = await _tasksManager.RetryAsync(orgSlug, dsSlug, id, ct) });

    // ---- POST /tasks/{id}/delete -----------------------------------------

    [HttpPost("{id}/delete", Name = nameof(TasksController) + "." + nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
        => Ok(new { deleted = await _tasksManager.DeleteAsync(orgSlug, dsSlug, id, ct) });
}