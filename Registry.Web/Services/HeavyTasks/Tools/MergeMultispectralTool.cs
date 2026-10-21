#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Services.HeavyTasks.Models;
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
    public sealed record Parameters(string[]? Paths, string? OutputPath);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.NonBlankStrings("paths"),
        p.Text("outputPath"));


    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["paths", "outputPath"],
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Single-band raster entry paths to merge, in band order." },
            "outputPath": { "type": "string", "description": "Output multi-band COG file name (dataset-relative)." }
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

        return Task.CompletedTask;
    }

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

        progress.Report(new HeavyToolProgress(-1, "merging",
            LogChunk: $"Merging {paths.Length} bands → {outputPath}"));

        // Overwrite an existing output (parity with the legacy manager).
        var outputFullPath = ctx.Ddb.GetLocalPath(outputPath);
        if (File.Exists(outputFullPath))
            File.Delete(outputFullPath);

        await Task.Run(() => ctx.Ddb.MergeMultispectral(paths, outputPath), ct);

        ct.ThrowIfCancellationRequested();

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
