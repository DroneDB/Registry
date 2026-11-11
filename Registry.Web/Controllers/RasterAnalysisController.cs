using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Raster analysis and processing of a dataset's entries: raster description and statistics, renders with
/// visualization parameters, GeoTIFF export, multispectral merge, alignment check, values at points, along
/// lines and over areas, contour lines and orthophoto border masking. Stockpiles have their own controller.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix)]
[Produces("application/json")]
public class RasterAnalysisController : ControllerBaseEx
{
    private readonly RasterAnalysisService _raster;
    private readonly RasterProcessingService _processing;
    private readonly IObjectRenderService _renders;

    public RasterAnalysisController(RasterAnalysisService raster, RasterProcessingService processing,
        IObjectRenderService renders)
    {
        _raster = raster;
        _processing = processing;
        _renders = renders;
    }

    // The checked paths of a merge request ("" for a null element: the service refuses an empty path)
    private static string[] PathsOf(MergeMultispectralRequestDto request) =>
        Array.ConvertAll(request.Paths ?? [], p => p?.ToString() ?? string.Empty);

    #region Multispectral

    /// <summary>Get raster info (bands, sensor profile, presets) for a file.</summary>
    [HttpGet("raster-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path, CancellationToken ct)
    {
        var json = await _raster.GetRasterInfoAsync(orgSlug, dsSlug, path.ToString(), ct: ct);
        return Content(json, "application/json");
    }

    /// <summary>Get raster statistics/histogram for a band or formula.</summary>
    [HttpGet("raster-metadata", Name = nameof(ObjectsController) + "." + nameof(GetRasterMetadata))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterMetadata(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter, CancellationToken ct)
    {
        var json = await _raster.GetRasterMetadataAsync(orgSlug, dsSlug, path.ToString(), formula, bandFilter, ct: ct);
        return Content(json, "application/json");
    }

    /// <summary>Generate thumbnail with extended visualization params.</summary>
    [HttpGet("thumb-ex", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnailEx))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateThumbnailEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery] int? size,
        [FromQuery] string? preset,
        [FromQuery] string? bands,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter,
        [FromQuery] string? colormap,
        [FromQuery] string? rescale,
        [FromQuery] double? nodata,
        [FromQuery] string? rescaleUnit, CancellationToken ct)
    {
        var res = await _renders.GenerateThumbnailDataExAsync(orgSlug, dsSlug, path.ToString(), size,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale, nodata, rescaleUnit), ct: ct);

        if (res == null) return NotFound();
        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Generate tile with extended visualization params.</summary>
    [HttpGet("tiles-ex/{tz:int}/{tx:int}/{tyRaw}.{ext:regex(^(" + TileFormat.RouteExtensions + ")$)}",
        Name = nameof(ObjectsController) + "." + nameof(GenerateTileEx))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateTileEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] int tz,
        [FromRoute, Required] int tx,
        [FromRoute, Required] string tyRaw,
        [FromQuery, Required] DatasetPath path,
        [FromRoute, Required] string ext,
        [FromQuery] string? preset,
        [FromQuery] string? bands,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter,
        [FromQuery] string? colormap,
        [FromQuery] string? rescale,
        [FromQuery] double? nodata,
        [FromQuery] string? rescaleUnit, CancellationToken ct)
    {
        var retina = tyRaw.EndsWith("@2x", StringComparison.Ordinal);
        if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
            throw new BadRequestException("Invalid input parameters (retina indicator should be '@2x')");

        var res = await _renders.GenerateTileDataExAsync(orgSlug, dsSlug, path.ToString(), tz, tx, ty, retina,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale, nodata, rescaleUnit),
            TileFormat.FromExtension(ext), ct: ct);

        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Export raster with visualization params applied as GeoTIFF.</summary>
    [HttpGet("export", Name = nameof(ObjectsController) + "." + nameof(ExportRaster))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExportRaster(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery] string? preset = null,
        [FromQuery] string? bands = null,
        [FromQuery] string? formula = null,
        [FromQuery] string? bandFilter = null,
        [FromQuery] string? colormap = null,
        [FromQuery] string? rescale = null,
        [FromQuery] double? nodata = null,
        [FromQuery] string? rescaleUnit = null, CancellationToken ct = default)
    {
        var res = await _raster.ExportRasterAsync(orgSlug, dsSlug, path.ToString(),
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale, nodata, rescaleUnit), ct: ct);

        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Validate merge-multispectral inputs.</summary>
    [HttpPost("merge-multispectral/validate",
        Name = nameof(ObjectsController) + "." + nameof(ValidateMergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateMergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request, CancellationToken ct)
    {
        var json = await _processing.ValidateMergeMultispectralAsync(orgSlug, dsSlug, PathsOf(request), ct: ct);
        return Content(json, "application/json");
    }

    /// <summary>Preview merge-multispectral result.</summary>
    [HttpPost("merge-multispectral/preview",
        Name = nameof(ObjectsController) + "." + nameof(PreviewMergeMultispectral))]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewMergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request, CancellationToken ct)
    {
        var data = await _processing.PreviewMergeMultispectralAsync(orgSlug, dsSlug, PathsOf(request),
            request.PreviewBands, request.ThumbSize ?? 512, ct: ct);
        return File(data, "image/webp", "preview.webp");
    }

    /// <summary>Merge single-band rasters into a multi-band COG.</summary>
    [HttpPost("merge-multispectral", Name = nameof(ObjectsController) + "." + nameof(MergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OkDto>> MergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request, CancellationToken ct)
    {
        await _processing.MergeMultispectralAsync(orgSlug, dsSlug, PathsOf(request), request.OutputPath,
            request.Overwrite, ct: ct);
        return OkDto.Success;
    }

    /// <summary>Validate that source and reference rasters are compatible for alignment.</summary>
    [HttpPost("align/validate",
        Name = nameof(ObjectsController) + "." + nameof(ValidateAlignRaster))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateAlignRaster(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] AlignRasterRequestDto request, CancellationToken ct)
    {
        var json = await _processing.ValidateAlignRasterAsync(orgSlug, dsSlug,
            request.SourcePath, request.ReferencePath, ct: ct);
        return Content(json, "application/json");
    }

    #endregion

    #region Raster Analysis

    /// <summary>Get the value info of a raster file: range, unit, NoData, default stretch and export size.</summary>
    /// <remarks>
    /// The range is exact, in the units of the band, with the thermal calibration if applicable. The info also has the
    /// NoData value that applies, the default colour stretch (<c>stretch</c>, D-041) and the size of an export with its limit
    /// (<c>estimatedExportBytes</c>, <c>exportLimitBytes</c>, D-023).
    /// </remarks>
    /// <param name="nodata">A NoData value that replaces the one of the band (FR-RAS-31).</param>
    /// <param name="formula">A vegetation index: the stretch is that of the index (D-041).</param>
    /// <param name="bbox">A WGS 84 window <c>minLon,minLat,maxLon,maxLat</c>: the estimate is that of the window.</param>
    [HttpGet("raster-value-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterValueInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterValueInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery] double? nodata,
        [FromQuery] string? formula,
        [FromQuery] string? bbox, CancellationToken ct)
    {
        var options = RasterOptionRules.Options(nodata, formula, RasterOptionRules.ParseBbox(bbox));
        var json = await _raster.GetRasterValueInfoAsync(orgSlug, dsSlug, path.ToString(), options, ct: ct);
        return Content(json, "application/json");
    }

    /// <summary>Get the raster value (temperature, elevation, ...) at a point: map coordinates or a pixel.</summary>
    /// <remarks>
    /// Map coordinates (<c>lon</c>, <c>lat</c>, WGS 84) are converted to the pixel of the file by libddb. An R-JPEG, which has
    /// no geotransform, takes the pixel (<c>x</c>, <c>y</c>) of its raw thermal grid. Both at once is a 400, and so are pixel
    /// coordinates on a georeferenced raster (<c>errors.x</c>).
    /// </remarks>
    [HttpGet("raster-point-value", Name = nameof(ObjectsController) + "." + nameof(GetRasterPointValue))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterPointValue(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery] double? lon,
        [FromQuery] double? lat,
        [FromQuery] int? x,
        [FromQuery] int? y,
        [FromQuery] double? nodata,
        [FromQuery] string? formula, CancellationToken ct)
    {
        var hasMap = lon.HasValue || lat.HasValue;
        var hasPixel = x.HasValue || y.HasValue;
        if (hasMap && hasPixel)
            throw new BadRequestException("Send map coordinates (lon, lat) or pixel coordinates (x, y), not both");

        string json;
        if (hasMap)
        {
            if (!lon.HasValue || !lat.HasValue)
                throw new BadRequestException("Both 'lon' and 'lat' are required");
            json = await _raster.GetRasterPointValueAsync(orgSlug, dsSlug, path.ToString(), lon.Value, lat.Value,
                RasterOptionRules.Options(nodata, formula), ct: ct);
        }
        else
        {
            if (!x.HasValue || !y.HasValue)
                throw new BadRequestException("Either 'lon' and 'lat' (WGS 84) or 'x' and 'y' (pixels) are required");
            json = await _raster.GetRasterPointValueAsync(orgSlug, dsSlug, path.ToString(), x.Value, y.Value,
                RasterOptionRules.Options(nodata, formula), ct: ct);
        }

        return Content(json, "application/json");
    }

    /// <summary>Get raster value statistics for an area: a WGS 84 window or a rectangle of pixels.</summary>
    /// <remarks>
    /// A WGS 84 window (<c>bbox</c> = <c>minLon,minLat,maxLon,maxLat</c>) gives the pixels of the file under it. An R-JPEG,
    /// which has no geotransform, takes a rectangle of pixels (<c>x0</c>, <c>y0</c>, <c>x1</c>, <c>y1</c>) of its raw thermal
    /// grid. Both at once is a 400, and so are pixel coordinates on a georeferenced raster (<c>errors.x0</c>).
    /// </remarks>
    [HttpGet("raster-area-stats", Name = nameof(ObjectsController) + "." + nameof(GetRasterAreaStats))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterAreaStats(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery] string? bbox,
        [FromQuery] int? x0,
        [FromQuery] int? y0,
        [FromQuery] int? x1,
        [FromQuery] int? y1,
        [FromQuery] double? nodata,
        [FromQuery] string? formula, CancellationToken ct)
    {
        var hasMap = !string.IsNullOrWhiteSpace(bbox);
        var hasPixel = x0.HasValue || y0.HasValue || x1.HasValue || y1.HasValue;
        if (hasMap && hasPixel)
            throw new BadRequestException("Send a map window (bbox) or pixel coordinates (x0, y0, x1, y1), not both");

        string json;
        if (hasMap)
        {
            json = await _raster.GetRasterAreaStatsAsync(orgSlug, dsSlug, path.ToString(),
                RasterOptionRules.ParseBbox(bbox)!, RasterOptionRules.Options(nodata, formula), ct: ct);
        }
        else
        {
            if (!(x0.HasValue && y0.HasValue && x1.HasValue && y1.HasValue))
                throw new BadRequestException("Either 'bbox' (WGS 84) or 'x0', 'y0', 'x1' and 'y1' (pixels) are required");
            json = await _raster.GetRasterAreaStatsAsync(orgSlug, dsSlug, path.ToString(), x0.Value, y0.Value, x1.Value,
                y1.Value, RasterOptionRules.Options(nodata, formula), ct: ct);
        }

        return Content(json, "application/json");
    }

    /// <summary>Sample raster values along a GeoJSON LineString (WGS84) and return a profile.</summary>
    /// <remarks>
    /// Accepts the geometry via POST body to support arbitrarily long polylines.
    /// Returns JSON with equispaced samples containing distance (meters), value, lon, lat.
    /// </remarks>
    [HttpPost("raster-profile", Name = nameof(ObjectsController) + "." + nameof(GetRasterProfile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterProfile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] RasterProfileRequestDto request, CancellationToken ct)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        var json = await _raster.GetRasterProfileAsync(orgSlug, dsSlug, request.Path.ToString(),
            request.LineString, request.Samples ?? 256, RasterOptionRules.Options(request.Nodata, request.Formula), ct: ct);
        return Content(json, "application/json");
    }

    #endregion

    #region Contour Lines

    /// <summary>
    /// Generate contour lines (GeoJSON LineStrings with an `elev` property) from a DEM/DSM/DTM raster.
    /// </summary>
    /// <remarks>
    /// Either <c>interval</c> or <c>count</c> must be supplied. When both are set, <c>interval</c> wins. The result says
    /// when libddb cut the lines at its cap (<c>truncated</c>, <c>featureCap</c>). The Hub does not call this
    /// endpoint: it runs the heavy task <c>raster-contours</c> (progress and Cancel, FR-RAS-11), which gives the same GeoJSON.
    /// </remarks>
    [HttpPost("contours", Name = nameof(ObjectsController) + "." + nameof(GenerateContours))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateContours(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] ContourRequestDto request, CancellationToken ct)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        var json = await _raster.GenerateContoursAsync(orgSlug, dsSlug, request.Path.ToString(),
            new ContourOptions(request.Interval, request.Count, request.BaseOffset, request.MinElev, request.MaxElev,
                request.SimplifyTolerance, request.BandIndex, request.Nodata, request.Formula), ct: ct);
        return Content(json, "application/json");
    }

    #endregion

    #region MaskBorders

    /// <summary>Check if a masked version of the file already exists.</summary>
    [HttpPost("mask-borders/check", Name = nameof(ObjectsController) + "." + nameof(CheckMaskBorders))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MaskBordersCheckResponseDto>> CheckMaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request, CancellationToken ct)
    {
        var result = await _processing.CheckMaskedFileExistsAsync(orgSlug, dsSlug, request.Path.ToString(), ct: ct);
        return result;
    }

    /// <summary>Start masking orthophoto borders to make them transparent.</summary>
    [HttpPost("mask-borders", Name = nameof(ObjectsController) + "." + nameof(MaskBorders))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OkDto>> MaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request, CancellationToken ct)
    {
        await _processing.MaskBordersAsync(orgSlug, dsSlug, request.Path.ToString(), request.Near, request.White,
            request.Overwrite, ct: ct);
        return OkDto.Success;
    }

    #endregion
}
