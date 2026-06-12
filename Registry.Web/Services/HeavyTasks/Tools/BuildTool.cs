#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool wrapping DroneDB derivative build (spec §4.11). Produces no
/// downloadable artifact; it mutates the dataset build cache in place. This is a
/// thin forwarder over the pre-existing <see cref="HangfireUtils.BuildWrapper"/>
/// so the migration of the 8 legacy enqueue sites is behaviour-preserving.
/// </summary>
public sealed class BuildTool : IHeavyTool
{
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

    public string Id => "build";
    public string Version => "1";
    public string Title => "Build";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
        => new(EstimatedOutputBytes: null, QuotaKey: "build", DefaultFileName: null, ContentType: null);

    public Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        System.IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var force = false;
        if (request.Params.ValueKind == JsonValueKind.Object &&
            request.Params.TryGetProperty("force", out var forceEl) &&
            (forceEl.ValueKind == JsonValueKind.True || forceEl.ValueKind == JsonValueKind.False))
        {
            force = forceEl.GetBoolean();
        }

        var path = request.Path;

        progress.Report(new HeavyToolProgress(-1, "building", LogChunk: $"Building '{path ?? "(pending)"}'"));

        if (string.IsNullOrEmpty(path))
            HangfireUtils.BuildPendingWrapper(ctx.Ddb, ctx.Hangfire);
        else
            HangfireUtils.BuildWrapper(ctx.Ddb, path!, force, ctx.Hangfire);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: "Build complete"));
        return Task.FromResult<HeavyToolArtifact?>(null);
    }
}
