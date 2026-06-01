#nullable enable
using System.Security.Claims;
using System.Text.Json;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>Outcome codes for a quota evaluation (maps to HTTP status).</summary>
public enum HeavyTaskQuotaCode
{
    Ok = 0,
    TooLarge = 413,
    Exceeded = 429
}

/// <summary>Result of a pre-submit quota evaluation.</summary>
public sealed record HeavyTaskQuotaResult(HeavyTaskQuotaCode Code, string? Message = null)
{
    public bool IsAllowed => Code == HeavyTaskQuotaCode.Ok;
    public static HeavyTaskQuotaResult Ok { get; } = new(HeavyTaskQuotaCode.Ok);
}

/// <summary>A request to submit a heavy task, as resolved by the controller.</summary>
public sealed record HeavyTaskSubmitRequest(
    string OrgSlug,
    string DsSlug,
    string ToolId,
    string? Version,
    string? Path,
    JsonElement Params,
    bool Force,
    string? UserId,
    ClaimsPrincipal? Caller,
    string? Hash = null,
    string? ParentJobId = null,
    string? WorkflowExecutionId = null);

/// <summary>Result of a submit (either a freshly enqueued task or a dedup hit).</summary>
public sealed record HeavyTaskSubmitResult(
    string TaskId,
    string ToolId,
    string Version,
    bool Deduplicated,
    long? EstimatedOutputBytes);
