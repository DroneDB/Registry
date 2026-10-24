#nullable enable
using System;
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

    /// <summary>Reads the tool's parameters (absent or ill-typed values are null; see <see cref="ToolParameters"/>).</summary>
    protected abstract TParams ReadParams(ToolParameters p);

    protected abstract Task ValidateAsync(HeavyToolRequest request, TParams p, IHeavyToolValidationContext ctx,
        CancellationToken ct);

    protected abstract HeavyToolPlan Plan(HeavyToolRequest request, TParams p, IHeavyToolValidationContext ctx);

    protected abstract Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, TParams p,
        IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct);
}
