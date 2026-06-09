#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that rescans the dataset index, clears caches and rebuilds the
/// derivative products (spec §A.3). Offloads the blocking full-file scan from the
/// request thread. Mutates the dataset in place (no downloadable artifact).
/// Injects <see cref="IServiceScopeFactory"/> to reach the scoped
/// <see cref="IObjectsManager"/> for Redis cache invalidation (spec §1.1).
/// </summary>
public sealed class RescanIndexTool : IHeavyTool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "types": { "type": ["string", "null"], "description": "Comma-separated entry types to rescan (e.g. \"image,geoimage\"). Omit for all." },
            "stopOnError": { "type": ["boolean", "null"], "default": false, "description": "Stop on the first entry error." }
          },
          "additionalProperties": false
        }
        """);

    private readonly IServiceScopeFactory _scopeFactory;

    public RescanIndexTool(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public string Id => "rescan-index";
    public string Version => "1";
    public string Title => "Rescan index";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
        => Task.CompletedTask;

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
        => new(EstimatedOutputBytes: null, QuotaKey: "rescan-index", DefaultFileName: null, ContentType: null);

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var types = ReadString(request.Params, "types");
        var stopOnError = ReadBool(request.Params, "stopOnError") ?? false;

        progress.Report(new HeavyToolProgress(-1, "rescanning",
            LogChunk: $"Rescanning index (types: {types ?? "all"})"));
        var results = ctx.Ddb.RescanIndex(types, stopOnError);

        ct.ThrowIfCancellationRequested();

        progress.Report(new HeavyToolProgress(-1, "clearing-cache", LogChunk: "Clearing build cache"));
        ctx.Ddb.ClearBuildCache();

        // Redis tile/thumbnail cache lives behind the scoped IObjectsManager.
        using (var scope = _scopeFactory.CreateScope())
        {
            var objects = scope.ServiceProvider.GetRequiredService<IObjectsManager>();
            await objects.InvalidateAllDatasetCaches(request.OrgSlug, request.DsSlug);
        }

        var total = results.Count;
        var ok = results.Count(r => r.Success);
        var err = total - ok;

        // Rebuild derivatives inline (same approach as BuildTool); no continuation.
        progress.Report(new HeavyToolProgress(-1, "rebuilding",
            LogChunk: $"Rescan: {ok}/{total} ok, {err} errors. Rebuilding derivatives"));
        HangfireUtils.BuildWrapper(ctx.Ddb, null, true, ctx.Hangfire);
        HangfireUtils.BuildPendingWrapper(ctx.Ddb, ctx.Hangfire);

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Rescan complete: {ok}/{total} ok, {err} errors"));
        return null;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool? ReadBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
