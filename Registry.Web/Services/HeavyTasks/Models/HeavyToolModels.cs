using System.Security.Claims;
using System.Text.Json;
using Hangfire.Server;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Builds;

namespace Registry.Web.Services.HeavyTasks.Models;

/// <summary>
/// Access level a tool requires on the target dataset.
/// </summary>
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

/// <summary>
/// Context available to a tool during validation and planning.
/// </summary>
public interface IHeavyToolValidationContext
{
    IDDB Ddb { get; }
    ClaimsPrincipal? Caller { get; }

    /// <summary>
    /// The output file an earlier attempt of this task already wrote (<see cref="PublishedOutputMarker"/>), or null
    /// (at submit, and on the first attempt). A tool that refuses an existing output does not refuse this one.
    /// </summary>
    string? PublishedOutput { get; }

    /// <summary>
    /// Whether the task was queued before tools refused an existing output without <c>overwrite</c> (its record
    /// declares no output paths): a request of then without the <c>overwrite</c> field replaced its output, and still
    /// does when it runs. Always false at submit.
    /// </summary>
    bool QueuedBeforeOverwriteRule { get; }
}

/// <summary>
/// Context available to a tool during execution.
/// </summary>
public interface IHeavyToolExecutionContext : IHeavyToolValidationContext
{
    string TaskId { get; }
    string? WorkDir { get; }    // null when ProducesArtifact = false
    PerformContext Hangfire { get; }

    /// <summary>The task's dataset, as a background job names it.</summary>
    DatasetKey Dataset { get; }

    /// <summary>Queues the builds of what the tool added (as children of the task), in the task's scope.</summary>
    BuildScheduler BuildQueue { get; }

    /// <summary>Runs builds inline, in the task's own job (the build and rescan tools).</summary>
    DdbJobs Builds { get; }

    /// <summary>
    /// Records that the task wrote its output file <paramref name="path"/> (<see cref="PublishedOutputMarker"/>):
    /// a later attempt skips the write and indexes it. Called right after the write returns.
    /// </summary>
    void MarkOutputPublished(string path);
}
