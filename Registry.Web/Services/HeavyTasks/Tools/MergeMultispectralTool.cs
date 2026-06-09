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

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that merges single-band rasters into a multi-band COG (spec §A.2).
/// CPU-bound (GDAL); offloaded from the request thread. Mutates the dataset in
/// place (re-indexes the merged output) so it produces no downloadable artifact.
/// Mirrors the legacy <c>ObjectsManager.MergeMultispectral</c> behaviour exactly.
/// </summary>
public sealed class MergeMultispectralTool : IHeavyTool
{
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

    public string Id => "merge-multispectral";
    public string Version => "1";
    public string Title => "Merge multispectral bands";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var paths = ReadStringArray(request.Params, "paths");
        if (paths == null || paths.Length < 2)
            throw new ArgumentException("At least two band paths are required.");

        var outputPath = ReadString(request.Params, "outputPath");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.");

        // Path-traversal validation (parity with ObjectsManager.MergeMultispectral).
        var root = ctx.Ddb.DatasetFolderPath;
        CommonUtils.ValidateRelativePath(outputPath, root);
        CommonUtils.ValidateRelativePaths(paths, root);

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

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var paths = ReadStringArray(request.Params, "paths") ?? [];
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

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var paths = ReadStringArray(request.Params, "paths")
                    ?? throw new InvalidOperationException("Band paths are required.");
        var outputPath = ReadString(request.Params, "outputPath")
                         ?? throw new InvalidOperationException("An output path is required.");

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
        return list.ToArray();
    }

    private static string[]? ReadStringArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }

        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
