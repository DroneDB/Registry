#nullable enable
using System.Security.Claims;
using System.Text.Json;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.HeavyTasks.Models;

/// <summary>Access level a tool requires on the target dataset.</summary>
public enum HeavyToolPermission
{
    Read,
    Write
}

/// <summary>A request to execute a heavy tool against a dataset.</summary>
public sealed record HeavyToolRequest(
    string ToolId,
    string Version,
    string OrgSlug,
    string DsSlug,
    string? Path,
    JsonElement Params);

/// <summary>
/// Cheap (O(ms)) pre-submit plan: estimated output size, quota bucket and the
/// default artifact descriptor. Used for quota checks and the pre-submit estimate.
/// </summary>
public sealed record HeavyToolPlan(
    long? EstimatedOutputBytes,
    string QuotaKey,
    string? DefaultFileName,
    string? ContentType);

/// <summary>Descriptor of the artifact produced by a tool execution.</summary>
public sealed record HeavyToolArtifact(
    string RelativePath,
    string ContentType,
    string FileName,
    long SizeBytes,
    string? Sha256 = null);

/// <summary>Incremental progress emitted by a tool during execution.</summary>
public sealed record HeavyToolProgress(
    double Fraction,            // 0..1, or -1 for indeterminate
    string? Phase = null,
    string? Message = null,
    string? LogChunk = null);   // additional text to append to the log tail

/// <summary>Context available to a tool during validation and planning.</summary>
public interface IHeavyToolValidationContext
{
    IDDB Ddb { get; }
    ClaimsPrincipal? Caller { get; }
    ILogger Logger { get; }
}

/// <summary>Context available to a tool during execution.</summary>
public interface IHeavyToolExecutionContext : IHeavyToolValidationContext
{
    string TaskId { get; }
    string? WorkDir { get; }    // null when ProducesArtifact = false
    PerformContext Hangfire { get; }
}
