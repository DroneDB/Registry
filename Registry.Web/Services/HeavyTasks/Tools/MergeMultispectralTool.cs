using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that merges single-band rasters into a multi-band COG.
/// CPU-bound (GDAL); offloaded from the request thread. Mutates the dataset in
/// place (re-indexes the merged output) so it produces no downloadable artifact.
/// Mirrors the legacy <c>ObjectsManager.MergeMultispectral</c> behaviour exactly.
/// </summary>
public sealed class MergeMultispectralTool(DatasetPathPolicy pathPolicy) : HeavyToolBase<MergeMultispectralTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string[]? Paths, string? OutputPath, bool? Overwrite);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.NonBlankStrings("paths"),
        p.Text("outputPath"),
        p.Flag("overwrite"));


    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["paths", "outputPath"],
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Single-band raster entry paths to merge, in band order." },
            "outputPath": { "type": "string", "description": "Output multi-band COG file name (dataset-relative)." },
            "overwrite": { "type": "boolean", "default": false, "description": "Replace the output file when it exists (without it, an existing output is refused with 409)." }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => "merge-multispectral";
    public override string Version => "1";
    public override string Title => "Merge multispectral bands";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var paths = args.Paths;
        if (paths == null || paths.Length < 2)
            throw new ArgumentException("At least two band paths are required.");

        var outputPath = args.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.");

        // libddb's path rule (parity with ObjectsManager.MergeMultispectral)
        outputPath = pathPolicy.Normalize(outputPath, "output path");
        paths = Array.ConvertAll(paths, p => pathPolicy.Normalize(p));

        // Native validation of the band set (CRS/size/type compatibility).
        var json = ctx.Ddb.ValidateMergeMultispectral(paths);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) &&
                    okEl.ValueKind == JsonValueKind.False)
                {
                    var errors = ReadErrors(doc.RootElement);
                    throw new ArgumentException(
                        errors.Length > 0
                            ? $"Invalid multispectral merge: {string.Join("; ", errors)}"
                            : "Invalid multispectral merge.");
                }
            }
            catch (JsonException)
            {
                // If the native validation output is not parseable, defer to execution.
            }
        }

        // An existing output is replaced only when the request says so (409 otherwise, D-040); the output an
        // earlier attempt of this task wrote is its own
        if (!PublishedOutputMarker.Covers(ctx.Ddb, ctx.PublishedOutput, outputPath))
            DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, args.Overwrite ?? ctx.QueuedBeforeOverwriteRule);

        return Task.CompletedTask;
    }

    protected override IReadOnlyList<string> InputPaths(Parameters p) =>
        p.Paths is null ? [] : Array.ConvertAll(p.Paths, x => pathPolicy.Normalize(x));

    protected override IReadOnlyList<string> OutputPaths(Parameters p) =>
        string.IsNullOrWhiteSpace(p.OutputPath) ? [] : [pathPolicy.Normalize(p.OutputPath, "output path")];

    protected override string? Label(Parameters p) =>
        string.IsNullOrWhiteSpace(p.OutputPath) ? null : "\u2192 " + pathPolicy.Normalize(p.OutputPath, "output path");

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var paths = args.Paths ?? [];
            long total = 0;
            foreach (var p in paths)
            {
                var entry = ctx.Ddb.GetEntry(p);
                if (entry is { Size: > 0 }) total += entry.Size;
            }
            if (total > 0) estimate = total;
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: "merge-multispectral",
            DefaultFileName: null, ContentType: null);
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // Validated at submit; normalized again on the worker (a job enqueued by an older version)
        var paths = Array.ConvertAll(args.Paths
                    ?? throw new InvalidOperationException("Band paths are required."), p => pathPolicy.Normalize(p));
        var outputPath = pathPolicy.Normalize(args.OutputPath, "output path");

        var published = ctx.PublishedOutput;
        if (PublishedOutputMarker.Covers(ctx.Ddb, published, outputPath))
        {
            // An earlier attempt wrote the output and failed after it (the index was busy): index it, never write again
            outputPath = published!;
            progress.Report(new HeavyToolProgress(-1, "indexing",
                LogChunk: $"'{outputPath}' was written by an earlier attempt"));
        }
        else
        {
            // Checked again on the worker: a file may have appeared since the submit. A name that differs only in
            // letter case is the existing file (D-040)
            outputPath = DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, args.Overwrite ?? ctx.QueuedBeforeOverwriteRule);

            progress.Report(new HeavyToolProgress(-1, "merging",
                LogChunk: $"Merging {paths.Length} bands → {outputPath}"));

            // NOTE: libddb 2.0 refuses an existing output: the request asked for the replace
            var outputFullPath = ctx.Ddb.GetLocalPath(outputPath);
            if (File.Exists(outputFullPath))
                File.Delete(outputFullPath);

            await Task.Run(() => ctx.Ddb.MergeMultispectral(paths, outputPath), ct);

            // The very next step after the write: from here a later attempt indexes the output, and no
            // cancellation check stands between the write and the index. NOTE: a process crash between the
            // write and this line leaves the file without the marker (the retry then refuses it as existing)
            ctx.MarkOutputPublished(outputPath);
        }

        progress.Report(new HeavyToolProgress(-1, "indexing", LogChunk: "Indexing merged output"));
        ctx.Ddb.AddRaw(outputPath);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: "Merge complete"));
        return null;
    }

    private static string[] ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errEl) || errEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var item in errEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
        }
        return [.. list];
    }


}
