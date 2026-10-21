#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that exports a single raster entry to GeoTIFF applying the
/// visualization params (preset/bands/formula/colormap/rescale). Uses the
/// block-windowed <c>DDBExportRaster2</c> path (bounded peak memory) with
/// incremental progress and cooperative cancellation.
/// </summary>
public sealed class RasterExportTool : HeavyToolBase<RasterExportTool.Parameters>
{
    /// <summary>The visualization parameters and the optional output file name.</summary>
    public sealed record Parameters(string? Preset, string? Bands, string? Formula, string? BandFilter,
        string? Colormap, string? Rescale, string? FileName);

    protected override Parameters ReadParams(ToolParameters p) => new(p.String("preset"), p.String("bands"),
        p.String("formula"), p.String("bandFilter"), p.String("colormap"), p.String("rescale"), p.String("fileName"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["path"],
          "properties": {
            "path": { "type": "string", "description": "Entry path of the source raster." },
            "preset": { "type": ["string", "null"] },
            "bands": { "type": ["string", "null"] },
            "formula": { "type": ["string", "null"] },
            "bandFilter": { "type": ["string", "null"] },
            "colormap": { "type": ["string", "null"] },
            "rescale": { "type": ["string", "null"] },
            "fileName": { "type": ["string", "null"], "description": "Optional output file name." }
          },
          "additionalProperties": false
        }
        """);

    private readonly int _defaultTileSize;

    public RasterExportTool(IOptions<ProcessingPlatformSettings> platformSettings)
    {
        _defaultTileSize = platformSettings.Value.DefaultRasterTileSize;
        if (_defaultTileSize < 1) _defaultTileSize = 512;
    }

    public override string Id => "raster-export";
    public override string Version => "1";
    public override string Title => "Export raster (GeoTIFF)";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public override bool ProducesArtifact => true;
    public override bool RunsNativeBuild => true;
    public override string? ResultExtension => "tif";
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A source raster 'path' is required.");

        if (!ctx.Ddb.EntryExists(request.Path!))
            throw new ArgumentException($"Source raster '{request.Path}' does not exist in the dataset.");

        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx)
    {
        // Cheap estimate: GeoTIFF output is roughly the size of the source entry.
        long? estimate = null;
        try
        {
            var entry = ctx.Ddb.GetEntry(request.Path!);
            if (entry is not null && entry.Size > 0)
                estimate = entry.Size;
        }
        catch
        {
            // estimate is best-effort
        }

        var fileName = SafeFileName(p.FileName,
                           $"{Path.GetFileNameWithoutExtension(request.Path)}_export.tif");

        return new HeavyToolPlan(estimate, QuotaKey: "raster-export", DefaultFileName: fileName, ContentType: "image/tiff");
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters p,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException("RasterExportTool requires a work directory.");

        var fileName = SafeFileName(p.FileName,
                           $"{Path.GetFileNameWithoutExtension(request.Path)}_export.tif");
        var outputPath = Path.Combine(ctx.WorkDir, fileName);


        progress.Report(new HeavyToolProgress(0, "loading", LogChunk: $"Exporting '{request.Path}' to {fileName}"));

        // Surface phase transitions from the native exporter as log lines so the
        // Task History "View log" dialog shows meaningful progress for the export.
        var lastReportedPhase = "loading";
        await Task.Run(() => ctx.Ddb.ExportRaster(
            request.Path!, outputPath, p.Preset, p.Bands, p.Formula, p.BandFilter, p.Colormap, p.Rescale,
            _defaultTileSize,
            (fraction, phase) =>
            {
                string? logChunk = null;
                if (!string.IsNullOrEmpty(phase) && phase != lastReportedPhase)
                {
                    lastReportedPhase = phase;
                    var pct = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
                    logChunk = $"{phase} ({pct}%)";
                }
                progress.Report(new HeavyToolProgress(fraction, phase, LogChunk: logChunk));
            },
            ct), ct);

        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(outputPath);
        if (!info.Exists)
            throw new InvalidOperationException("Raster export produced no output file.");

        var sha = await ComputeSha256Async(outputPath, ct);
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Export complete ({info.Length} bytes)"));

        return new HeavyToolArtifact(
            RelativePath: fileName,
            ContentType: "image/tiff",
            FileName: fileName,
            SizeBytes: info.Length,
            Sha256: sha);
    }

    // Strips any directory components (path traversal / absolute path) from a
    // user-supplied file name, falling back to `fallback` if the result is empty.
    private static string SafeFileName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var safe = Path.GetFileName(name.Trim());
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
