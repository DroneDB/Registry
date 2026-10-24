#nullable enable
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Managers;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>The tool catalog of a dataset and the submission of a tool run (RQ-12, out of <c>TasksManager</c>).</summary>
public interface ITaskSubmission
{
    /// <summary>The tools available for the dataset, with the per-organization feature gating applied (read access).</summary>
    Task<TaskToolDto[]> GetToolsAsync(string orgSlug, string dsSlug, CancellationToken ct = default);

    /// <summary>Validates and enqueues a tool run, deduplicating against an equivalent in-flight task.</summary>
    Task<SubmitTaskResponseDto> SubmitAsync(string orgSlug, string dsSlug, SubmitTaskRequestDto body,
        CancellationToken ct = default);
}

/// <summary>Default <see cref="ITaskSubmission"/>.</summary>
public sealed class TaskSubmission : ITaskSubmission
{
    private readonly IHeavyToolRegistry _registry;
    private readonly IHeavyToolGating _gating;
    private readonly IDatasetAccessResolver _access;
    private readonly ICurrentUserTaskSubmitter _submitter;

    public TaskSubmission(IHeavyToolRegistry registry, IHeavyToolGating gating, IDatasetAccessResolver access,
        ICurrentUserTaskSubmitter submitter)
    {
        _registry = registry;
        _gating = gating;
        _access = access;
        _submitter = submitter;
    }

    public async Task<TaskToolDto[]> GetToolsAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

        return await Task.WhenAll(_registry.All.Select(async t =>
        {
            var st = await _gating.EvaluateAsync(t.Id, orgSlug);
            return new TaskToolDto(t.Id, t.Version, t.Title,
                t.RequiredAccess.ToString(), t.ProducesArtifact, t.InputSchema.RootElement,
                st.Hidden, st.Disabled, st.DisabledMessage);
        }));
    }

    public async Task<SubmitTaskResponseDto> SubmitAsync(string orgSlug, string dsSlug, SubmitTaskRequestDto body,
        CancellationToken ct = default)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ToolId))
            throw new BadRequestException("toolId is required");

        var tool = _registry.Resolve(body.ToolId, body.Version);
        if (tool is null)
            throw new BadRequestException($"Tool '{body.ToolId}' is not available");

        // Feature gating: reject hidden/disabled tools (or role/org-denied) server-side,
        // independent of the client UI.
        var gatingState = await _gating.EvaluateAsync(tool.Id, orgSlug);
        if (!gatingState.Allowed)
            throw new ForbiddenException(
                gatingState.DisabledMessage ?? $"Tool '{tool.Id}' is not available.", noRetry: true);

        var requiredAccess = tool.RequiredAccess == HeavyToolPermission.Write ? AccessType.Write : AccessType.Read;
        await _access.ResolveAsync(orgSlug, dsSlug, requiredAccess, ct: ct);

        // A task without parameters takes an empty object
        var paramsElement = body.Params ?? JsonSerializer.SerializeToElement(new { });

        var result = await _submitter.SubmitAsync(orgSlug, dsSlug, tool.Id, tool.Version, body.Path, paramsElement,
            body.Force, ct);

        var baseUrl = $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{result.TaskId}";
        return new SubmitTaskResponseDto(
            result.TaskId, result.ToolId, result.Version, result.Deduplicated,
            baseUrl, baseUrl + "/result", result.EstimatedOutputBytes);
    }
}
