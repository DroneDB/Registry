#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that aligns a source GeoTIFF to a reference GeoTIFF and indexes
/// the output in the dataset. CPU-bound (GDAL); offloaded from the request thread.
/// Mutates the dataset in place (re-indexes the aligned output) – no downloadable
/// artifact is produced. The caller (frontend) is responsible for submitting a
/// separate "build" task for the output file after this task succeeds.
/// </summary>
public sealed class AlignRasterTool : IHeavyTool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["sourcePath", "referencePath", "outputPath"],
          "properties": {
            "sourcePath":    { "type": "string", "description": "Dataset-relative path of the GeoTIFF to align." },
            "referencePath": { "type": "string", "description": "Dataset-relative path of the reference GeoTIFF." },
            "outputPath":    { "type": "string", "description": "Dataset-relative path for the aligned output COG." },
            "mode":          { "type": "string", "enum": ["similarity", "translation"], "default": "similarity",
                               "description": "Alignment mode: similarity (4-DOF) or translation (2-DOF, fast)." }
          },
          "additionalProperties": false
        }
        """);

    public string Id => "align-raster";
    public string Version => "1";
    public string Title => "Align GeoTIFF to reference";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var sourcePath    = ReadString(request.Params, "sourcePath");
        var referencePath = ReadString(request.Params, "referencePath");
        var outputPath    = ReadString(request.Params, "outputPath");

        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required.");
        if (string.IsNullOrWhiteSpace(referencePath))
            throw new ArgumentException("referencePath is required.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath is required.");

        var root = ctx.Ddb.DatasetFolderPath;
        CommonUtils.ValidateRelativePath(sourcePath, root);
        CommonUtils.ValidateRelativePath(referencePath, root);
        CommonUtils.ValidateRelativePath(outputPath, root);

        // Native validation (CRS, overlap, type compatibility)
        var validationJson = ctx.Ddb.ValidateAlignRaster(sourcePath, referencePath);
        if (!string.IsNullOrWhiteSpace(validationJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(validationJson);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) &&
                    okEl.ValueKind == JsonValueKind.False)
                {
                    var errors = ReadErrors(doc.RootElement);
                    throw new ArgumentException(
                        errors.Length > 0
                            ? $"Alignment not possible: {string.Join("; ", errors)}"
                            : "Alignment validation failed.");
                }
            }
            catch (JsonException)
            {
                // If native output is not parseable, defer to execution phase.
            }
        }

        return Task.CompletedTask;
    }

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var sourcePath = ReadString(request.Params, "sourcePath");
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var entry = ctx.Ddb.GetEntry(sourcePath);
                if (entry is { Size: > 0 }) estimate = entry.Size;
            }
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: "align-raster",
            DefaultFileName: null, ContentType: null);
    }

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var sourcePath    = ReadString(request.Params, "sourcePath")
                            ?? throw new InvalidOperationException("sourcePath is required.");
        var referencePath = ReadString(request.Params, "referencePath")
                            ?? throw new InvalidOperationException("referencePath is required.");
        var outputPath    = ReadString(request.Params, "outputPath")
                            ?? throw new InvalidOperationException("outputPath is required.");
        var mode          = ReadString(request.Params, "mode") ?? "similarity";

        progress.Report(new HeavyToolProgress(-1, "aligning",
            LogChunk: $"Aligning '{sourcePath}' → '{outputPath}' (mode: {mode})"));

        // Overwrite an existing output (parity with ObjectsManager.AlignRaster).
        var outputFullPath = ctx.Ddb.GetLocalPath(outputPath);
        if (File.Exists(outputFullPath))
            File.Delete(outputFullPath);

        ct.ThrowIfCancellationRequested();

        var resultJson = await Task.Run(
            () => ctx.Ddb.AlignRaster(sourcePath, referencePath, outputPath, mode), ct);

        ct.ThrowIfCancellationRequested();

        progress.Report(new HeavyToolProgress(-1, "indexing", LogChunk: "Indexing aligned output"));
        ctx.Ddb.AddRaw(outputPath);

        // Emit compact (single-line) JSON so the log tail delivers it as a single
        // entry that the frontend can JSON.parse after stripping the timestamp prefix.
        using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
        var compact = System.Text.Json.JsonSerializer.Serialize(doc);
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: compact));
        return null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string[]  ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errEl) || errEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new System.Collections.Generic.List<string>();
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

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
