using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// A heavy tool with typed parameters: <typeparamref name="TParams"/> is read once per call from the
/// request (<see cref="ReadParams"/>, through <see cref="ToolParameters"/>) and handed to the tool's
/// typed validate, plan and execute.
/// </summary>
public abstract class HeavyToolBase<TParams> : IHeavyTool where TParams : class
{
    public abstract string Id { get; }
    public abstract string Version { get; }
    public abstract string Title { get; }
    public abstract HeavyToolPermission RequiredAccess { get; }
    public abstract bool ProducesArtifact { get; }
    public virtual bool RunsNativeBuild => false;

    /// <inheritdoc cref="IHeavyTool.AddsDatasetFiles"/>
    public virtual bool AddsDatasetFiles => false;
    public virtual string? ResultExtension => null;
    public abstract JsonDocument InputSchema { get; }

    /// <summary>The typed parameters of <paramref name="request"/>.</summary>
    public TParams Params(HeavyToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadParams(new ToolParameters(request.Params));
    }

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct) =>
        ValidateAsync(request, Params(request), ctx, ct);

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx) =>
        Plan(request, Params(request), ctx);

    public Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress, CancellationToken ct) =>
        ExecuteAsync(request, Params(request), ctx, progress, ct);

    /// <inheritdoc cref="IHeavyTool.InputPaths"/>
    public IReadOnlyList<string> InputPaths(HeavyToolRequest request) => InputPaths(Params(request));

    /// <inheritdoc cref="IHeavyTool.SourcesDeletedAfterCommit"/>
    public IReadOnlyList<string> SourcesDeletedAfterCommit(HeavyToolRequest request) =>
        SourcesDeletedAfterCommit(Params(request));

    /// <inheritdoc cref="IHeavyTool.OutputPaths"/>
    public IReadOnlyList<string> OutputPaths(HeavyToolRequest request) => OutputPaths(Params(request));

    /// <inheritdoc cref="IHeavyTool.Label"/>
    public string? Label(HeavyToolRequest request) => Label(Params(request));

    /// <summary>The files the task reads (normalized); none unless the tool overrides it.</summary>
    /// <returns>The input paths.</returns>
    protected virtual IReadOnlyList<string> InputPaths(TParams p) => [];

    /// <summary>The files the task deletes after it commits (normalized); none unless the tool overrides it.</summary>
    /// <returns>The sources deleted after the commit.</returns>
    protected virtual IReadOnlyList<string> SourcesDeletedAfterCommit(TParams p) => [];

    /// <summary>The files the task writes (normalized); none unless the tool overrides it.</summary>
    /// <returns>The output paths.</returns>
    protected virtual IReadOnlyList<string> OutputPaths(TParams p) => [];

    /// <summary>The label of the task's target files; none unless the tool overrides it.</summary>
    /// <returns>The label, or null.</returns>
    protected virtual string? Label(TParams p) => null;

    /// <summary>Reads the tool's parameters (absent or ill-typed values are null; see <see cref="ToolParameters"/>).</summary>
    protected abstract TParams ReadParams(ToolParameters p);

    protected abstract Task ValidateAsync(HeavyToolRequest request, TParams p, IHeavyToolValidationContext ctx,
        CancellationToken ct);

    protected abstract HeavyToolPlan Plan(HeavyToolRequest request, TParams p, IHeavyToolValidationContext ctx);

    protected abstract Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, TParams p,
        IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct);
}
