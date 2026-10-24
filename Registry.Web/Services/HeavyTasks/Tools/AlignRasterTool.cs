#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that aligns a source GeoTIFF to a reference GeoTIFF and indexes
/// the output in the dataset. CPU-bound (GDAL); offloaded from the request thread.
/// Mutates the dataset in place (re-indexes the aligned output) – no downloadable
/// artifact is produced. The caller (frontend) is responsible for submitting a
/// separate "build" task for the output file after this task succeeds.
/// </summary>
public sealed class AlignRasterTool(DatasetPathPolicy paths) : HeavyToolBase<AlignRasterTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? SourcePath, string? ReferencePath, string? OutputPath, string? Mode);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("sourcePath"),
        p.Text("referencePath"),
        p.Text("outputPath"),
        p.Text("mode"));


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

    public override string Id => "align-raster";
    public override string Version => "1";
    public override string Title => "Align GeoTIFF to reference";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var sourcePath = args.SourcePath;
        var referencePath = args.ReferencePath;
        var outputPath = args.OutputPath;

        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required.");
        if (string.IsNullOrWhiteSpace(referencePath))
            throw new ArgumentException("referencePath is required.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath is required.");

        sourcePath = paths.Normalize(sourcePath, "source path");
        referencePath = paths.Normalize(referencePath, "reference path");
        outputPath = paths.Normalize(outputPath, "output path");

        // Native validation (CRS, overlap, type compatibility)
        var validationJson = ctx.Ddb.ValidateAlignRaster(sourcePath, referencePath);
        if (string.IsNullOrWhiteSpace(validationJson)) return Task.CompletedTask;

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

        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var sourcePath = args.SourcePath;
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

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // Validated at submit; normalized again on the worker (a job enqueued by an older version)
        var sourcePath = paths.Normalize(args.SourcePath, "source path");
        var referencePath = paths.Normalize(args.ReferencePath, "reference path");
        var outputPath = paths.Normalize(args.OutputPath, "output path");
        var mode = args.Mode ?? "similarity";

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
        using var doc = JsonDocument.Parse(resultJson);
        var compact = JsonSerializer.Serialize(doc);
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: compact));
        return null;
    }

    private static string[] ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errEl) || errEl.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. from item in errEl.EnumerateArray()
            where item.ValueKind == JsonValueKind.String
            select item.GetString()
            into s
            where !string.IsNullOrWhiteSpace(s)
            select s!
        ];
    }

}