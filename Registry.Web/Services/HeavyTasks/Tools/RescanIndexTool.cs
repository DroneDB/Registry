#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that rescans the dataset index, clears caches and rebuilds the
/// derivative products. Offloads the blocking full-file scan from the
/// request thread. Mutates the dataset in place (no downloadable artifact).
/// With a type or extension filter only the rescanned entries are rebuilt
/// (see <see cref="RescanRebuild"/>, resolved through <see cref="IServiceScopeFactory"/>).
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
            "extensions": { "type": ["string", "null"], "description": "Comma-separated file extensions to rescan (e.g. \"dwg,dxf\"). Omit for all. An entry must match both filters." },
            "stopOnError": { "type": ["boolean", "null"], "default": false, "description": "Stop on the first entry error." }
          },
          "additionalProperties": false
        }
        """);

    private const string TypesParam = "types";
    private const string ExtensionsParam = "extensions";
    private const string StopOnErrorParam = "stopOnError";

    private readonly IServiceScopeFactory _scopeFactory;

    public RescanIndexTool(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public string Id => HeavyToolIds.RescanIndex;
    public string Version => "1";
    public string Title => "Rescan index";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public bool RunsNativeBuild => true;
    public JsonDocument InputSchema => Schema;

    /// <summary>The params of a request that rescans the entries <paramref name="filter"/> selects.</summary>
    public static JsonElement CreateParams(RescanFilter filter, bool stopOnError) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [TypesParam] = filter.TypesArgument,
            [ExtensionsParam] = filter.ExtensionsArgument,
            [StopOnErrorParam] = stopOnError
        });

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        ReadFilter(request); // throws ArgumentException (400) on an unknown type or an invalid extension
        return Task.CompletedTask;
    }

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
        => new(EstimatedOutputBytes: null, QuotaKey: HeavyToolIds.RescanIndex, DefaultFileName: null, ContentType: null);

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var filter = ReadFilter(request);
        var stopOnError = ReadBool(request.Params, StopOnErrorParam) ?? false;

        progress.Report(new HeavyToolProgress(-1, "rescanning",
            LogChunk: $"Rescanning index ({filter})"));
        var results = ctx.Ddb.RescanIndex(filter.ToOptions(stopOnError));

        ct.ThrowIfCancellationRequested();

        var ok = results.Count(r => r.Success);
        var summary = $"{ok}/{results.Count} ok, {results.Count - ok} errors";
        progress.Report(new HeavyToolProgress(-1, "rebuilding", LogChunk: $"Rescan: {summary}. Rebuilding derivatives"));

        // RescanRebuild resolves the IDatasetCacheInvalidator, registered on every host
        // (including processing nodes) unlike the full IObjectsManager.
        using var scope = _scopeFactory.CreateScope();
        var rebuild = await scope.ServiceProvider.GetRequiredService<RescanRebuild>().RunAsync(
            request.OrgSlug, request.DsSlug, ctx.Ddb, filter, results, new InlineBuilds(ctx),
            f => progress.Report(new HeavyToolProgress(f, "rebuilding")), ct);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: rebuild.WholeDataset
            ? $"Rescan complete: {summary}"
            : $"Rescan complete: {summary}; rebuilt {rebuild.Built}, {rebuild.Failed} rebuild failures"));
        return null;
    }

    // Inline, one build at a time (same approach as BuildTool): this task already holds a
    // builds-queue worker, so a rescan across many datasets cannot flood the build queue.
    private sealed class InlineBuilds(IHeavyToolExecutionContext ctx) : IRescanBuildRunner
    {
        public void BuildDataset() => HangfireUtils.BuildWrapper(ctx.Ddb, null, true, ctx.Hangfire);

        public void BuildEntry(RescanResult entry) => HangfireUtils.BuildWrapper(ctx.Ddb, entry.Path, true, ctx.Hangfire);

        public void BuildPending() => HangfireUtils.BuildPendingWrapper(ctx.Ddb, ctx.Hangfire);
    }

    private static RescanFilter ReadFilter(HeavyToolRequest request) =>
        RescanFilter.Parse(ReadString(request.Params, TypesParam), ReadString(request.Params, ExtensionsParam));

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
