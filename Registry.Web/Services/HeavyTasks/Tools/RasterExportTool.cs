using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Objects;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that exports a single raster entry to GeoTIFF applying the
/// visualization params (preset/bands/formula/colormap/rescale). Uses the
/// block-windowed <c>DDBExportRaster2</c>/<c>DDBExportRaster3</c> path (bounded peak memory) with incremental progress and
/// cooperative cancellation. With <c>bbox</c> (WGS 84 <c>[minLon, minLat, maxLon, maxLat]</c>) it exports that window of
/// the raster at its native resolution ('Export visible extent', FR-RAS-16, D-023).
/// </summary>
public sealed class RasterExportTool : HeavyToolBase<RasterExportTool.Parameters>
{
    /// <summary>The visualization parameters and the optional output file name.</summary>
    /// <param name="Nodata">The NoData value that replaces the one of the bands (FR-RAS-31), as sent: checked at the submit.</param>
    /// <param name="Bbox">The WGS 84 window to export (FR-RAS-16), as sent: checked at the submit.</param>
    /// <param name="RescaleUnit">The temperature unit the rescale of a thermal formula is written in (D-022): libddb converts
    /// it to the unit of the formula. Checked at the submit (<see cref="RasterOptionRules.RescaleUnit"/>).</param>
    public sealed record Parameters(string? Preset, string? Bands, string? Formula, string? BandFilter,
        string? Colormap, string? Rescale, string? FileName, JsonElement? Nodata = null, JsonElement? Bbox = null,
        string? RescaleUnit = null);

    protected override Parameters ReadParams(ToolParameters p) => new(p.Text("preset"), p.Text("bands"),
        p.Text("formula"), p.Text("bandFilter"), p.Text("colormap"), p.Text("rescale"), p.Text("fileName"),
        p.Value("nodata"), p.Value("bbox"), p.Text("rescaleUnit"));

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
            "rescaleUnit": {
              "type": ["string", "null"],
              "description": "Temperature unit the rescale of a thermal formula is written in (K, C or F); converted to the unit of the formula."
            },
            "fileName": { "type": ["string", "null"], "description": "Optional output file name." },
            "nodata": { "type": ["number", "null"], "description": "NoData value that replaces the one of the bands." },
            "bbox": {
              "type": ["array", "null"],
              "items": { "type": "number" },
              "minItems": 4,
              "maxItems": 4,
              "description": "WGS 84 window to export (minLon, minLat, maxLon, maxLat), at the native resolution of the raster."
            }
          },
          "additionalProperties": false
        }
        """);

    private readonly int _defaultTileSize;
    private readonly IRasterExportSizeCheck _sizeCheck;

    public RasterExportTool(IOptions<ProcessingPlatformSettings> platformSettings, IRasterExportSizeCheck sizeCheck)
    {
        _defaultTileSize = platformSettings.Value.DefaultRasterTileSize;
        if (_defaultTileSize < 1) _defaultTileSize = 512;
        _sizeCheck = sizeCheck;
    }

    public override string Id => "raster-export";
    public override string Version => "1";
    public override string Title => "Export raster (GeoTIFF)";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public override bool ProducesArtifact => true;
    public override bool RunsNativeBuild => true;
    public override string? ResultExtension => "tif";
    public override JsonDocument InputSchema => Schema;

    protected override async Task ValidateAsync(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A source raster 'path' is required.");

        if (!ctx.Ddb.EntryExists(request.Path!))
            throw new ArgumentException($"Source raster '{request.Path}' does not exist in the dataset.");

        // A range that is not two numbers with min below max is a 400 at the submit (FR-RAS-10), not a failed export
        RescaleRange.Validated(p.Rescale);

        // The NoData value, the window (FR-RAS-16) and the unit of the rescale (D-022) are 400s at their own members
        var raster = RasterOptionsOf(p);
        RasterOptionRules.RescaleUnit(p.RescaleUnit);

        if (!string.IsNullOrWhiteSpace(p.Formula))
            ValidateFormula(ctx.Ddb, request.Path!, p.Formula!, p.BandFilter, ct);

        // At the submit (and the retry of a task), not on the worker: an export over the export limit is a 400, with the
        // estimate of the value info the Hub shows (FR-RAS-16, D-023)
        if (ctx is not IHeavyToolExecutionContext)
            await _sizeCheck.EnsureWithinLimitAsync(request.OrgSlug, request.DsSlug, request.Path!, raster, ct);
    }

    /// <summary>
    /// The NoData value and the window of the request as the options of the values (null when it has neither): the options
    /// of the export, and of the estimate that the submit checks against the limit (the estimate of the window, D-023).
    /// </summary>
    /// <exception cref="InvalidFieldException">A value that is not a finite number, or a window that is not four WGS 84 degrees.</exception>
    internal static RasterAnalysisOptions? RasterOptionsOf(Parameters p)
    {
        double? nodata = null;
        if (p.Nodata is { } nd)
        {
            if (nd.ValueKind != JsonValueKind.Number || !nd.TryGetDouble(out var value) || !double.IsFinite(value))
                throw new InvalidFieldException("nodata", "'nodata' must be a finite number");
            nodata = value;
        }

        double[]? bbox = null;
        if (p.Bbox is { } box)
        {
            const string rule = "'bbox' must be four finite numbers: minLon, minLat, maxLon, maxLat (WGS 84)";
            if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4)
                throw new InvalidFieldException("bbox", rule);
            bbox = new double[4];
            var i = 0;
            foreach (var item in box.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out bbox[i]) || !double.IsFinite(bbox[i]))
                    throw new InvalidFieldException("bbox", rule);
                i++;
            }

            try
            {
                RasterOptionRules.RequireWindow(bbox);
            }
            catch (BadRequestException ex)
            {
                throw new InvalidFieldException("bbox", ex.Message);
            }
        }

        return nodata is null && bbox is null ? null : new RasterAnalysisOptions(nodata, null, bbox);
    }

    /// <summary>
    /// Refuses (400) a formula the raster cannot compute before the task is queued, with the reason raster-metadata
    /// gives (FR-DEV-05): libddb refuses it itself (<see cref="DdbInvalidArgumentsException"/>: "Unknown formula:
    /// BOGUS", "Requires bands: RN"); libddb 2.0 computes it all the same and only marks it in its list of formulas
    /// (<c>disabled</c>, <c>disabledReason</c>), or fails with a plain error. Any other failure is left to the export,
    /// which says what is wrong with the file.
    /// </summary>
    private static void ValidateFormula(IDDB ddb, string path, string formula, string? bandFilter, CancellationToken ct)
    {
        string metadata;
        try
        {
            metadata = ddb.GetRasterMetadata(path, formula, bandFilter);
        }
        catch (DdbInvalidArgumentsException ex)
        {
            if (RendersAnyway(formula, ex.Message))
                return;
            throw new ArgumentException(FormulaRefusal(formula, ex.Message), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // libddb 2.0: the formula fails as a plain error; its list of formulas says why
            ct.ThrowIfCancellationRequested();
            try
            {
                metadata = ddb.GetRasterMetadata(path, null, bandFilter);
            }
            catch (Exception listEx) when (listEx is not OperationCanceledException)
            {
                return;
            }
        }

        if (ListedRefusal(metadata, formula) is { } refusal)
            throw new ArgumentException(refusal);
    }

    // The temperature formulas read the single band of a DEM or of a thermal raster without a sensor profile: libddb's
    // list of formulas marks them "Requires bands: T" (no band is mapped to T), while the rendering computes them from
    // the first band. A missing band is no refusal for them; any other reason is.
    private static readonly string[] SingleBandFormulas = ["CELSIUS", "KELVIN", "FAHRENHEIT"];

    private static bool RendersAnyway(string formula, string reason) =>
        SingleBandFormulas.Contains(formula, StringComparer.Ordinal) &&
        reason.StartsWith("Requires bands", StringComparison.OrdinalIgnoreCase);

    // The reason libddb gave, with the formula it is about
    private static string FormulaRefusal(string formula, string reason) =>
        reason.StartsWith("Unknown formula", StringComparison.OrdinalIgnoreCase)
            ? reason
            : $"{formula} is not available for this raster: {reason}";

    // Why the list of formulas of raster-metadata refuses formula (unknown or disabled), or null
    private static string? ListedRefusal(string metadata, string formula)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (!doc.RootElement.TryGetProperty("algorithms", out var algorithms) ||
                algorithms.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var algorithm in algorithms.EnumerateArray())
            {
                if (algorithm.ValueKind != JsonValueKind.Object ||
                    !algorithm.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    id.GetString() != formula)
                    continue;

                if (!algorithm.TryGetProperty("disabled", out var disabled) || disabled.ValueKind != JsonValueKind.True)
                    return null;

                var reason = algorithm.TryGetProperty("disabledReason", out var why) &&
                             why.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(why.GetString())
                    ? why.GetString()!
                    : "its bands are missing";
                return RendersAnyway(formula, reason) ? null : FormulaRefusal(formula, reason);
            }

            return $"Unknown formula: {formula}";
        }
        catch (JsonException)
        {
            // Not the shape of raster-metadata: the export decides
            return null;
        }
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

        var fileName = ToolFileNames.Safe(p.FileName,
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

        var fileName = ToolFileNames.Safe(p.FileName,
                           $"{Path.GetFileNameWithoutExtension(request.Path)}_export.tif");
        var outputPath = Path.Combine(ctx.WorkDir, fileName);


        progress.Report(new HeavyToolProgress(0, "loading", LogChunk: $"Exporting '{request.Path}' to {fileName}"));

        // Surface phase transitions from the native exporter as log lines so the
        // Task History "View log" dialog shows meaningful progress for the export.
        var lastReportedPhase = "loading";
        var raster = RasterOptionsOf(p);
        // The options of the export also carry the unit of the rescale (not those of the size estimate: no value changes)
        if (RasterOptionRules.RescaleUnit(p.RescaleUnit) is { } rescaleUnit)
            raster = (raster ?? RasterAnalysisOptions.None) with { RescaleUnit = rescaleUnit };
        await Task.Run(() => ctx.Ddb.ExportRaster(
            request.Path!, outputPath, p.Preset, p.Bands, p.Formula, p.BandFilter, p.Colormap, RescaleRange.Validated(p.Rescale),
            _defaultTileSize, raster,
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

        // libddb writes the GeoTIFF (blocks at random offsets): read back for its hash
        var sha = await FileHash.Sha256Async(outputPath, ct);
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Export complete ({info.Length} bytes)"));

        return new HeavyToolArtifact(
            RelativePath: fileName,
            ContentType: "image/tiff",
            FileName: fileName,
            SizeBytes: info.Length,
            Sha256: sha);
    }
}
