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
/// incremental progress and cooperative cancellation (spec §4.11, §9.1).
/// </summary>
public sealed class RasterExportTool : IHeavyTool
{
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

    public RasterExportTool(IOptions<AppSettings> appSettings)
    {
        _defaultTileSize = appSettings.Value.ProcessingPlatform?.DefaultRasterTileSize ?? 512;
        if (_defaultTileSize < 1) _defaultTileSize = 512;
    }

    public string Id => "raster-export";
    public string Version => "1";
    public string Title => "Export raster (GeoTIFF)";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public bool ProducesArtifact => true;
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A source raster 'path' is required.");

        if (!ctx.Ddb.EntryExists(request.Path!))
            throw new ArgumentException($"Source raster '{request.Path}' does not exist in the dataset.");

        return Task.CompletedTask;
    }

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
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

        var fileName = ReadString(request.Params, "fileName")
                       ?? $"{Path.GetFileNameWithoutExtension(request.Path)}_export.tif";

        return new HeavyToolPlan(estimate, QuotaKey: "raster-export", DefaultFileName: fileName, ContentType: "image/tiff");
    }

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException("RasterExportTool requires a work directory.");

        var fileName = ReadString(request.Params, "fileName")
                       ?? $"{Path.GetFileNameWithoutExtension(request.Path)}_export.tif";
        var outputPath = Path.Combine(ctx.WorkDir, fileName);

        var preset = ReadString(request.Params, "preset");
        var bands = ReadString(request.Params, "bands");
        var formula = ReadString(request.Params, "formula");
        var bandFilter = ReadString(request.Params, "bandFilter");
        var colormap = ReadString(request.Params, "colormap");
        var rescale = ReadString(request.Params, "rescale");

        progress.Report(new HeavyToolProgress(0, "loading", LogChunk: $"Exporting '{request.Path}' to {fileName}"));

        await Task.Run(() => ctx.Ddb.ExportRaster(
            request.Path!, outputPath, preset, bands, formula, bandFilter, colormap, rescale,
            _defaultTileSize,
            (fraction, phase) => progress.Report(new HeavyToolProgress(fraction, phase)),
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

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
