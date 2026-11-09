using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Contract for a heavy (long-running, asynchronous) tool tracked in the
/// Processing Platform task substrate. Implementations must be stateless,
/// HTTP-context free, and cooperatively cancellable.
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
    /// Whether the tool runs a native build or raster processing inline (GDAL warps, COG/COPC
    /// builds). Such jobs run on the "builds" queue, whose worker count bounds concurrent
    /// builds. Tools that only enqueue build jobs keep the default.
    /// </summary>
    bool RunsNativeBuild => false;

    /// <summary>
    /// Whether the tool adds files to a dataset (FR-XC-26): photogrammetry, the imports, archive extract, Merge, Align and
    /// Mask. Such a tool records what it creates in the task's write record
    /// (<see cref="Models.IHeavyToolExecutionContext.WriteRecord"/>), and a cancelled, failed or lost task deletes it
    /// (FR-XC-27, D-045). False by default.
    /// </summary>
    bool AddsDatasetFiles => false;

    /// <summary>
    /// Default file extension (without the leading dot) of the produced artifact, or
    /// null when the tool produces none. Authoritative source for client result naming.
    /// </summary>
    string? ResultExtension => null;

    /// <summary>JSON Schema (2020-12) describing the tool's input parameters.</summary>
    JsonDocument InputSchema { get; }

    /// <summary>
    /// The dataset files the task reads, normalized (FR-XC-25). <see cref="Adapters.HeavyTaskRunner"/> refuses an output that
    /// is one of them. Invalid parameters may throw a 400 (the runner also asks before <see cref="ValidateAsync"/>).
    /// </summary>
    /// <returns>The input paths; empty by default.</returns>
    IReadOnlyList<string> InputPaths(HeavyToolRequest request) => [];

    /// <summary>
    /// The dataset files the task writes, normalized; compared as whole paths without regard to case (FR-XC-25, D-013, D-040): the task record
    /// stores them, and a submit whose output an active task of the dataset writes is refused. A tool whose output is
    /// a whole folder or the dataset declares none. Invalid parameters may throw a 400.
    /// </summary>
    /// <returns>The output paths; empty by default.</returns>
    IReadOnlyList<string> OutputPaths(HeavyToolRequest request) => [];

    /// <summary>
    /// The label of the task's target files, shown in the task lists (FR-XC-25, FR-PRC-22), stored in the task record. Asked after a
    /// successful <see cref="ValidateAsync"/>.
    /// </summary>
    /// <returns>The label, or null (none) by default.</returns>
    string? Label(HeavyToolRequest request) => null;

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
