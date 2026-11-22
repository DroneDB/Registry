using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that rescans selected entries of a dataset (files, or folders for the entries under them): it reads
/// them again and force-rebuilds the buildable ones (<see cref="RescanRebuild"/>, as any filtered rescan does). Unlike
/// <see cref="RescanIndexTool"/> it does not lock the dataset (it is not one of
/// <see cref="Datasets.DatasetActivity.LockingTools"/>): libddb commits each entry on its own, so the rest of the
/// dataset stays readable and writable while it runs. Mutates the dataset in place (no downloadable artifact).
/// </summary>
public sealed class RescanEntriesTool(DatasetPathPolicy paths, RescanExecution execution)
    : HeavyToolBase<RescanEntriesTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(IReadOnlyList<string>? Paths, bool? StopOnError);

    /// <summary>The name of the parameter that lists the entries (dataset paths).</summary>
    public const string PathsParam = "paths";

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Strings(PathsParam),
        p.Flag(RescanExecution.StopOnErrorParam));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["paths"],
          "properties": {
            "paths": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 1,
              "description": "Dataset-relative paths of the entries to rescan: a file, or a folder for the entries under it. The dataset root is refused."
            },
            {{RescanExecution.StopOnErrorSchemaProperty}}
          },
          "additionalProperties": false
        }
        """);

    public override string Id => HeavyToolIds.RescanEntries;
    public override string Version => "1";
    public override string Title => "Rescan entries";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;
    public override JsonDocument InputSchema => Schema;

    /// <summary>The params of a request that rescans the entries at <paramref name="entryPaths"/>.</summary>
    public static JsonElement CreateParams(IEnumerable<string> entryPaths, bool stopOnError) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [PathsParam] = entryPaths,
            [RescanExecution.StopOnErrorParam] = stopOnError
        });

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        RescanExecution.RequireNoRequestPath(request, $"Name the entries in the '{PathsParam}' parameter.");

        // 400 for no path and for a path that breaks the rule (the dataset root included), then 404 for a path that is not
        // indexed: a rescan of an entry the index does not know would only report it as an error
        foreach (var path in ReadFilter(args).Paths)
            DatasetEntryLookup.Require(ctx.Ddb, path);

        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
        => new(EstimatedOutputBytes: null, QuotaKey: HeavyToolIds.RescanEntries, DefaultFileName: null, ContentType: null);

    /// <inheritdoc />
    /// <remarks>The path for one entry, "N entries" for more.</remarks>
    protected override string? Label(Parameters p)
    {
        if (p.Paths is not { Count: > 0 })
            return null;

        try
        {
            var entries = ReadFilter(p).Paths;
            return entries.Count == 1 ? entries[0] : $"{entries.Count} entries";
        }
        catch (InvalidDatasetPathException)
        {
            // A label describes a task; it does not refuse one (the validation did)
            return null;
        }
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        await execution.RunAsync(request, ctx, ReadFilter(args),
            args.StopOnError ?? false, progress, ct);
        return null;
    }

    /// <summary>The filter of the request: its distinct normalized paths.</summary>
    /// <exception cref="ArgumentException">The request names no path.</exception>
    /// <exception cref="InvalidDatasetPathException">A path breaks the rule, or is the dataset root (400).</exception>
    private RescanFilter ReadFilter(Parameters args) => args.Paths is { Count: > 0 }
        ? RescanFilter.ForPaths(paths, args.Paths)
        : throw new ArgumentException($"{PathsParam} must name at least one entry.");
}
