using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool wrapping DroneDB derivative build. Produces no
/// downloadable artifact; it mutates the dataset build cache in place. It runs the build
/// inline in its task's job, as the build jobs do (<see cref="DdbJobs"/>). A build that finds files missing
/// returns as libddb defers it, without building: its log says which files it waits for, not that it is complete.
/// </summary>
public sealed class BuildTool : HeavyToolBase<BuildTool.Parameters>
{
    private readonly IBuildOutcomeClassifier _outcomes;

    public BuildTool(IBuildOutcomeClassifier outcomes)
    {
        _outcomes = outcomes ?? throw new System.ArgumentNullException(nameof(outcomes));
    }

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

        // The job succeeds either way: libddb's deferral is a normal return
        var waiting = _outcomes.Classify(ctx.Ddb, path, null, succeeded: true) is
            { Outcome: BuildOutcome.WaitingForFiles } deferred
            ? deferred.MissingFiles
            : null;
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: waiting == null
            ? "Build complete"
            : waiting.Count > 0 ? $"Waiting for {string.Join(", ", waiting)}" : "Waiting for files"));
        return Task.FromResult<HeavyToolArtifact?>(null);
    }
}
