using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that generates the contour lines of a raster (FR-RAS-11, NFR-001): every contour request of the Hub runs
/// as this task, so the progress and the Cancel come from the task (FR-XC-15). The result is the GeoJSON of
/// <c>POST contours</c> (a FeatureCollection of LineStrings with an <c>elev</c> property, plus <c>interval</c>,
/// <c>min</c>, <c>max</c>, <c>unit</c>, <c>rasterMin</c>, <c>rasterMax</c>, <c>truncated</c> and
/// <c>featureCap</c>), kept as the artifact of the task. The raster is read as the endpoint reads it: the file the user
/// uploaded, or the COG of its build when it has too many pixels to read in one piece (D-021: by the size the index holds,
/// or by libddb's refusal for an entry indexed without it, as <c>RasterAnalysisService</c> does). A cancel stops libddb
/// at its next checkpoint (the statistics pass, the GDAL generator, each line read back).
/// </summary>
public sealed class RasterContoursTool(IBuildArtifactResolver artifacts) : HeavyToolBase<RasterContoursTool.Parameters>
{
    /// <summary>The tool id (kebab-case, as <c>raster-export</c>).</summary>
    public const string ToolId = "raster-contours";

    /// <summary>The contour parameters of <c>POST contours</c> (absent or ill-typed: null) and the optional output file name.</summary>
    public sealed record Parameters(double? Interval, long? Count, double? BaseOffset, double? MinElev,
        double? MaxElev, double? SimplifyTolerance, long? BandIndex, double? Nodata, string? Formula,
        string? FileName);

    protected override Parameters ReadParams(ToolParameters p) => new(p.Number("interval"), p.WholeNumber("count"),
        p.Number("baseOffset"), p.Number("minElev"), p.Number("maxElev"), p.Number("simplifyTolerance"),
        p.WholeNumber("bandIndex"), p.Number("nodata"), p.Text("formula"), p.Text("fileName"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["path"],
          "properties": {
            "path": { "type": "string", "description": "Entry path of the raster." },
            "interval": { "type": ["number", "null"], "exclusiveMinimum": 0, "description": "Vertical spacing between levels (raster units). Wins over count." },
            "count": { "type": ["integer", "null"], "minimum": 1, "description": "Target number of levels, when interval is not set." },
            "baseOffset": { "type": ["number", "null"], "default": 0, "description": "Reference base elevation." },
            "minElev": { "type": ["number", "null"], "description": "Drop contours below this elevation." },
            "maxElev": { "type": ["number", "null"], "description": "Drop contours above this elevation." },
            "simplifyTolerance": { "type": ["number", "null"], "minimum": 0, "default": 0, "description": "Simplification tolerance in raster CRS units (0 = none)." },
            "bandIndex": { "type": ["integer", "null"], "minimum": 1, "default": 1, "description": "1-based band." },
            "nodata": { "type": ["number", "null"], "description": "A NoData value that replaces the one of the band." },
            "formula": { "type": ["string", "null"], "description": "A thermal formula gives the levels in its unit; an index formula is ignored." },
            "fileName": { "type": ["string", "null"], "description": "Optional output file name." }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => ToolId;
    public override string Version => "1";
    public override string Title => "Contour lines";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public override bool ProducesArtifact => true;
    public override bool RunsNativeBuild => true;
    public override string? ResultExtension => "geojson";
    public override JsonDocument InputSchema => Schema;

    /// <summary>The options of the endpoint (<see cref="ContourOptions"/>) of the parameters.</summary>
    internal static ContourOptions OptionsOf(Parameters p) => new(p.Interval,
        p.Count is { } count ? (int)Math.Clamp(count, int.MinValue, int.MaxValue) : null,
        p.BaseOffset ?? 0.0, p.MinElev, p.MaxElev, p.SimplifyTolerance ?? 0.0,
        p.BandIndex is { } band and > 0 ? (int)Math.Min(band, int.MaxValue) : 1, p.Nodata, p.Formula);

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A source raster 'path' is required.");

        if (!ctx.Ddb.EntryExists(request.Path!))
            throw new ArgumentException($"Source raster '{request.Path}' does not exist in the dataset.");

        // The rules of the endpoint, at the submit (and the retry of a task): a 400, not a failed task
        OptionsOf(p).Require();
        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx) =>
        // The size of the lines is not known before the generation
        new(null, QuotaKey: ToolId, DefaultFileName: FileNameOf(request, p), ContentType: "application/geo+json");

    private static string FileNameOf(HeavyToolRequest request, Parameters p) =>
        ToolFileNames.Safe(p.FileName, $"{Path.GetFileNameWithoutExtension(request.Path)}_contours.geojson");

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, Parameters p,
        IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException("RasterContoursTool requires a work directory.");

        var options = OptionsOf(p);
        options.Require();

        var fileName = FileNameOf(request, p);
        var outputPath = Path.Combine(ctx.WorkDir, fileName);
        var entry = ctx.Ddb.GetEntry(request.Path!) ?? throw new BadRequestException($"Invalid path: '{request.Path}'");
        var pixels = RasterAnalysisService.PixelsOf(entry);
        var source = pixels > RasterAnalysisService.MaxSourcePixels ? BuildSourceOf(ctx.Ddb, entry) : entry.Path;

        progress.Report(new HeavyToolProgress(0, "statistics", LogChunk: $"Contour lines of '{request.Path}'"));

        string json;
        try
        {
            json = await GenerateAsync(ctx.Ddb, source, options, progress, ct);
        }
        catch (InvalidOperationException ex) when (pixels is null && RasterAnalysisService.TooLargeToRead(ex))
        {
            // An entry indexed without its size: libddb's refusal tells it instead, as at the endpoint
            progress.Report(new HeavyToolProgress(0, "statistics",
                LogChunk: $"'{request.Path}' is too large to read in one piece: reading the COG of its build"));
            json = await GenerateAsync(ctx.Ddb, BuildSourceOf(ctx.Ddb, entry), options, progress, ct);
        }

        ct.ThrowIfCancellationRequested();

        var bytes = Encoding.UTF8.GetBytes(json);
        var summary = ContourSummary.Read(bytes);
        await File.WriteAllBytesAsync(outputPath, bytes, ct);
        var sha = await FileHash.Sha256Async(outputPath, ct);

        // A result cut at the cap says so, in the log and in the file (truncated, featureCap): FR-RAS-11
        var message = summary.Truncated
            ? $"Contours truncated at {summary.FeatureCap?.ToString(CultureInfo.InvariantCulture) ?? "the cap of"} lines: raise the interval or limit the elevation range"
            : $"Contours complete ({summary.FeatureCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} lines)";
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: message));

        return new HeavyToolArtifact(RelativePath: fileName, ContentType: "application/geo+json", FileName: fileName,
            SizeBytes: bytes.Length, Sha256: sha);
    }

    // The lines of one raster. The phase changes are log lines, as the raster export's; the fraction goes to the task at
    // each report
    private static Task<string> GenerateAsync(IDDB ddb, string source, ContourOptions options,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        var lastPhase = "statistics";
        return Task.Run(() => ddb.GenerateContours(source, options.Interval, options.Count,
            options.BaseOffset, options.MinElev, options.MaxElev, options.SimplifyTolerance, options.BandIndex,
            options.ValueOptions(),
            (fraction, phase) =>
            {
                string? logChunk = null;
                if (!string.IsNullOrEmpty(phase) && phase != lastPhase)
                {
                    lastPhase = phase;
                    logChunk = $"{phase} ({(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%)";
                }

                progress.Report(new HeavyToolProgress(fraction, phase, LogChunk: logChunk));
            }, ct), ct);
    }

    // The raster the endpoint reads instead of a source file that has more pixels than libddb reads in one piece (by the
    // size the index holds, or, for an entry indexed without it, by libddb's refusal): the COG of its build (D-021)
    private string BuildSourceOf(IDDB ddb, Entry entry) =>
        artifacts.GetRenderSource(ddb, entry)
        ?? throw new InvalidOperationException(
            $"'{entry.Path}' has too many pixels to read in one piece and its build has not finished: build the dataset first");
}

/// <summary>What the result of libddb says about itself, read without parsing its (large) list of lines.</summary>
internal readonly record struct ContourSummary(bool Truncated, long? FeatureCap, long? FeatureCount)
{
    /// <summary>The top-level <c>truncated</c>, <c>featureCap</c> and <c>featureCount</c> of the GeoJSON in <paramref name="utf8"/>.</summary>
    public static ContourSummary Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        var truncated = false;
        long? cap = null, count = null;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return default;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "truncated" when reader.TokenType is JsonTokenType.True or JsonTokenType.False:
                        truncated = reader.GetBoolean();
                        break;
                    case "featureCap" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var c):
                        cap = c;
                        break;
                    case "featureCount" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var n):
                        count = n;
                        break;
                    default:
                        // The list of lines is skipped whole
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not the shape of libddb's result: the Hub reads the file and says what is wrong with it
        }

        return new ContourSummary(truncated, cap, count);
    }
}
