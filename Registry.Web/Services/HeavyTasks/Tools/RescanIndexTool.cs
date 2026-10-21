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
using Registry.Web.Services.Jobs;
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
public sealed class RescanIndexTool : HeavyToolBase<RescanIndexTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? Types, string? Extensions, bool? StopOnError);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("types"),
        p.Text("extensions"),
        p.Flag("stopOnError"));


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

    public override string Id => HeavyToolIds.RescanIndex;
    public override string Version => "1";
    public override string Title => "Rescan index";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;
    public override JsonDocument InputSchema => Schema;

    /// <summary>The params of a request that rescans the entries <paramref name="filter"/> selects.</summary>
    public static JsonElement CreateParams(RescanFilter filter, bool stopOnError) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [TypesParam] = filter.TypesArgument,
            [ExtensionsParam] = filter.ExtensionsArgument,
            [StopOnErrorParam] = stopOnError
        });

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        ReadFilter(args); // throws ArgumentException (400) on an unknown type or an invalid extension
        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
        => new(EstimatedOutputBytes: null, QuotaKey: HeavyToolIds.RescanIndex, DefaultFileName: null, ContentType: null);

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var filter = ReadFilter(args);
        var stopOnError = args.StopOnError ?? false;

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
        public void BuildDataset() => ctx.Builds.BuildNow(ctx.Ddb, null, true, ctx.Hangfire);

        public void BuildEntry(RescanResult entry) => ctx.Builds.BuildNow(ctx.Ddb, entry.Path, true, ctx.Hangfire);

        public void BuildPending() => ctx.Builds.BuildPendingNow(ctx.Ddb, ctx.Hangfire);
    }

    private static RescanFilter ReadFilter(Parameters args) => RescanFilter.Parse(args.Types, args.Extensions);


}
