#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Contract for a heavy (long-running, asynchronous) tool tracked in the
/// Processing Platform task substrate. Implementations must be stateless,
/// HTTP-context free, and cooperatively cancellable (see spec §4.5).
/// </summary>
public interface IHeavyTool
{
    /// <summary>Stable kebab-case identifier (e.g. "build", "raster-export").</summary>
    string Id { get; }

    /// <summary>Pinned tool version ("1", "2", ...).</summary>
    string Version { get; }

    /// <summary>Human-readable title.</summary>
    string Title { get; }

    /// <summary>Access level required on the target dataset.</summary>
    HeavyToolPermission RequiredAccess { get; }

    /// <summary>Whether the tool produces a downloadable artifact.</summary>
    bool ProducesArtifact { get; }

    /// <summary>
    /// Default file extension (without the leading dot) of the produced artifact, or
    /// null when the tool produces none. Authoritative source for client result naming.
    /// </summary>
    string? ResultExtension => null;

    /// <summary>JSON Schema (2020-12) describing the tool's input parameters.</summary>
    JsonDocument InputSchema { get; }

    /// <summary>Validates the request. Must not mutate state; tolerates double invocation.</summary>
    Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct);

    /// <summary>Cheap (O(ms)) plan: output-size estimate, quota key and artifact descriptor.</summary>
    HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx);

    /// <summary>
    /// Executes the tool. Returns the produced artifact, or null for tools with
    /// <see cref="ProducesArtifact"/> = false. Must honor cancellation.
    /// </summary>
    Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct);
}
