using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool wrapping DroneDB derivative build. Produces no
/// downloadable artifact; it mutates the dataset build cache in place. It runs the build
/// inline in its task's job, as the build jobs do (<see cref="DdbJobs"/>).
/// </summary>
public sealed class BuildTool : HeavyToolBase<BuildTool.Parameters>
{
    /// <summary>Rebuild an entry that is already built.</summary>
    public sealed record Parameters(bool? Force);

    protected override Parameters ReadParams(ToolParameters p) => new(p.Flag("force"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Entry path to build (optional; empty builds pending)." },
            "force": { "type": "boolean", "default": false }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => "build";
    public override string Version => "1";
    public override string Title => "Build";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx,
        CancellationToken ct) => Task.CompletedTask;

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
        => new(EstimatedOutputBytes: null, QuotaKey: "build", DefaultFileName: null, ContentType: null);

    protected override Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        System.IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var force = args.Force ?? false;

        var path = request.Path;

        progress.Report(new HeavyToolProgress(-1, "building", LogChunk: $"Building '{path ?? "(pending)"}'"));

        if (string.IsNullOrEmpty(path))
        {
            ctx.Builds.BuildPendingNow(ctx.Ddb, ctx.Hangfire);
            progress.Report(new HeavyToolProgress(1, "done", LogChunk: "Pending build check complete"));
            return Task.FromResult<HeavyToolArtifact?>(null);
        }

        ctx.Builds.BuildNow(ctx.Ddb, path, force, ctx.Hangfire);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: "Build complete"));
        return Task.FromResult<HeavyToolArtifact?>(null);
    }
}
