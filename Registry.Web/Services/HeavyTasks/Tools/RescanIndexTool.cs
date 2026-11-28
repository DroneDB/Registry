using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that rescans the dataset index, clears caches and queues the rebuild of the
/// derivative products as a build task, a child of the rescan (<see cref="RescanExecution"/>). Offloads the blocking
/// full-file scan from the request thread. Mutates the dataset in place (no downloadable artifact).
/// With a type or extension filter only the rescanned entries are rebuilt
/// (see <see cref="RescanRebuild"/>). The rescan locks the dataset until it ends (<see cref="RescanEntriesTool"/>
/// rescans selected entries without).
/// </summary>
public sealed class RescanIndexTool : HeavyToolBase<RescanIndexTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? Types, string? Extensions, bool? StopOnError);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("types"),
        p.Text("extensions"),
        p.Flag(RescanExecution.StopOnErrorParam));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "types": { "type": ["string", "null"], "description": "Comma-separated entry types to rescan (e.g. \"image,geoimage\"). Omit for all." },
            "extensions": { "type": ["string", "null"], "description": "Comma-separated file extensions to rescan (e.g. \"dwg,dxf\"). Omit for all. An entry must match both filters." },
            {{RescanExecution.StopOnErrorSchemaProperty}}
          },
          "additionalProperties": false
        }
        """);

    private const string TypesParam = "types";
    private const string ExtensionsParam = "extensions";

    private readonly RescanExecution _execution;

    public RescanIndexTool(RescanExecution execution) => _execution = execution;

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
            [RescanExecution.StopOnErrorParam] = stopOnError
        });

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        RescanExecution.RequireNoRequestPath(request, $"Use the tool '{HeavyToolIds.RescanEntries}' to rescan some entries.");
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
        await _execution.RunAsync(request, ctx, ReadFilter(args), args.StopOnError ?? false, progress, ct);
        return null;
    }

    private static RescanFilter ReadFilter(Parameters args) => RescanFilter.Parse(args.Types, args.Extensions);
}
