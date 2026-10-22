#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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

    public TasksController(ITasksManager tasksManager)
    {
        _tasksManager = tasksManager;
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromBody] SubmitTaskRequestDto body, CancellationToken ct)
    {
        var response = await _tasksManager.SubmitAsync(orgSlug, dsSlug, body, ct);

        // Dedup hit returns 200; fresh enqueue returns 202.
        return response.Deduplicated ? Ok(response) : Accepted(response.StatusUrl, response);
    }

    // ---- GET /tasks -------------------------------------------------------

    [HttpGet(Name = nameof(TasksController) + "." + nameof(List))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> List(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromQuery] string? toolId, [FromQuery] string? state,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var page = await _tasksManager.ListPageAsync(orgSlug, dsSlug, toolId, state, skip, take, ct);

        // Polling clients send the tag they have: an unchanged list costs a 304, not the list (HUB-05)
        return OkUnlessNotModified(page.Items, page.ETag, "private, no-cache");
    }

    // ---- GET /tasks/events ------------------------------------------------

    /// <summary>Seconds between keep-alive comments of the task event stream.</summary>
    public const int EventStreamHeartbeatSeconds = 25;

    /// <summary>
    /// Server-sent events: a <c>tasks</c> event whenever the dataset's task list changes (HUB-05).
    /// The Hub refreshes the list (a conditional GET) on each event instead of polling. A comment
    /// line is sent every <see cref="EventStreamHeartbeatSeconds"/> seconds so proxies keep the
    /// connection open; <c>X-Accel-Buffering: no</c> keeps nginx from buffering the stream.
    /// </summary>
    [HttpGet("events", Name = nameof(TasksController) + "." + nameof(Events))]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task Events([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        CancellationToken ct)
    {
        var changed = System.Threading.Channels.Channel.CreateBounded<bool>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite
            });
        using var subscription = await _tasksManager.SubscribeAsync(orgSlug, dsSlug, () => changed.Writer.TryWrite(true));

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Reconnect after 5 s if the stream drops
        await Response.WriteAsync("retry: 5000\n\n", ct);
        await Response.Body.FlushAsync(ct);

        var id = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(EventStreamHeartbeatSeconds));
                bool hasChange;
                try
                {
                    hasChange = await changed.Reader.WaitToReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    hasChange = false;
                }

                if (hasChange && changed.Reader.TryRead(out _))
                    await Response.WriteAsync($"id: {++id}\nevent: tasks\ndata: {{}}\n\n", ct);
                else
                    await Response.WriteAsync(": ping\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away
        }
    }

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
        return ValidatedPhysicalFile(artifact.FilePath, artifact.ContentType, artifact.ETag, artifact.FileName);
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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