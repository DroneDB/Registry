#nullable enable
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
        [FromQuery] string? rescale, CancellationToken ct)
    {
        var res = await _renders.GenerateThumbnailDataExAsync(orgSlug, dsSlug, path.ToString(), size,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale), ct: ct);

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
        [FromQuery] string? rescale, CancellationToken ct)
    {
        var retina = tyRaw.EndsWith("@2x", StringComparison.Ordinal);
        if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
            throw new BadRequestException("Invalid input parameters (retina indicator should be '@2x')");

        var res = await _renders.GenerateTileDataExAsync(orgSlug, dsSlug, path.ToString(), tz, tx, ty, retina,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale),
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
        [FromQuery] string? rescale = null, CancellationToken ct = default)
    {
        var res = await _raster.ExportRasterAsync(orgSlug, dsSlug, path.ToString(),
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale), ct: ct);

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
    public async Task<ActionResult<OkDto>> MergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request, CancellationToken ct)
    {
        await _processing.MergeMultispectralAsync(orgSlug, dsSlug, PathsOf(request), request.OutputPath, ct: ct);
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

    /// <summary>Get raster value info (min/max/unit/dimensions), including thermal calibration if applicable.</summary>
    [HttpGet("raster-value-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterValueInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterValueInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path, CancellationToken ct)
    {
        var json = await _raster.GetRasterValueInfoAsync(orgSlug, dsSlug, path.ToString(), ct: ct);
        return Content(json, "application/json");
    }

    /// <summary>Get raster value (temperature/elevation/etc.) at a specific pixel location.</summary>
    [HttpGet("raster-point-value", Name = nameof(ObjectsController) + "." + nameof(GetRasterPointValue))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterPointValue(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery, Required] int x,
        [FromQuery, Required] int y, CancellationToken ct)
    {
        var json = await _raster.GetRasterPointValueAsync(orgSlug, dsSlug, path.ToString(), x, y, ct: ct);
        return Content(json, "application/json");
    }

    /// <summary>Get raster value statistics for a rectangular area.</summary>
    [HttpGet("raster-area-stats", Name = nameof(ObjectsController) + "." + nameof(GetRasterAreaStats))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterAreaStats(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] DatasetPath path,
        [FromQuery, Required] int x0,
        [FromQuery, Required] int y0,
        [FromQuery, Required] int x1,
        [FromQuery, Required] int y1, CancellationToken ct)
    {
        var json = await _raster.GetRasterAreaStatsAsync(orgSlug, dsSlug, path.ToString(), x0, y0, x1, y1, ct: ct);
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
            request.LineString, request.Samples ?? 256, ct: ct);
        return Content(json, "application/json");
    }

    #endregion

    #region Contour Lines

    /// <summary>
    /// Generate contour lines (GeoJSON LineStrings with an `elev` property) from a DEM/DSM/DTM raster.
    /// </summary>
    /// <remarks>
    /// Either <c>interval</c> or <c>count</c> must be supplied. When both are set, <c>interval</c> wins.
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
                request.SimplifyTolerance, request.BandIndex), ct: ct);
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
    public async Task<ActionResult<OkDto>> MaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request, CancellationToken ct)
    {
        await _processing.MaskBordersAsync(orgSlug, dsSlug, request.Path.ToString(), request.Near, request.White, ct: ct);
        return OkDto.Success;
    }

    #endregion
}
