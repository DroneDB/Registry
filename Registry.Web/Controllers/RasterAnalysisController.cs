#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug)]
[Produces("application/json")]
public class RasterAnalysisController : ControllerBaseEx
{
    private readonly IRasterAnalysisService _raster;
    private readonly IRasterProcessingService _processing;
    private readonly IObjectRenderService _renders;
    private readonly DatasetPathPolicy _paths;

    public RasterAnalysisController(IRasterAnalysisService raster, IRasterProcessingService processing,
        IObjectRenderService renders, DatasetPathPolicy paths)
    {
        _raster = raster;
        _processing = processing;
        _renders = renders;
        _paths = paths;
    }

    // libddb's path rule before the service runs (RQ-11 replaces these with a DatasetPath model binder)
    private void EnsureValidPath(string path) => _paths.Normalize(path);

    private void EnsureValidPaths(string[]? paths, string? outputPath = null)
    {
        if (paths == null || paths.Length == 0)
            throw new BadRequestException("paths is required");
        foreach (var path in paths)
            _paths.Normalize(path, "input path");
        if (outputPath != null)
            _paths.Normalize(outputPath, "output path");
    }

    #region Multispectral

    /// <summary>Get raster info (bands, sensor profile, presets) for a file.</summary>
    [HttpGet("raster-info", Name = nameof(ObjectsController) + "." + nameof(GetRasterInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRasterInfo(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery, Required] string path)
    {
        EnsureValidPath(path);

        var json = await _raster.GetRasterInfoAsync(orgSlug, dsSlug, path);
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
        [FromQuery, Required] string path,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter)
    {
        EnsureValidPath(path);

        var json = await _raster.GetRasterMetadataAsync(orgSlug, dsSlug, path, formula, bandFilter);
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
        [FromQuery, Required] string path,
        [FromQuery] int? size,
        [FromQuery] string? preset,
        [FromQuery] string? bands,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter,
        [FromQuery] string? colormap,
        [FromQuery] string? rescale)
    {
        EnsureValidPath(path);

        var res = await _renders.GenerateThumbnailDataExAsync(orgSlug, dsSlug, path, size,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale));

        if (res == null) return NotFound();
        return File(res.Data, res.ContentType, res.Name);
    }

    /// <summary>Generate tile with extended visualization params.</summary>
    [HttpGet("tiles-ex/{tz:int}/{tx:int}/{tyRaw}.{ext:regex(^(png|webp)$)}",
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
        [FromQuery, Required] string path,
        [FromRoute, Required] string ext,
        [FromQuery] string? preset,
        [FromQuery] string? bands,
        [FromQuery] string? formula,
        [FromQuery] string? bandFilter,
        [FromQuery] string? colormap,
        [FromQuery] string? rescale)
    {
        EnsureValidPath(path);

        var retina = tyRaw.EndsWith("@2x", StringComparison.Ordinal);
        if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
            throw new BadRequestException("Invalid input parameters (retina indicator should be '@2x')");

        var res = await _renders.GenerateTileDataExAsync(orgSlug, dsSlug, path, tz, tx, ty, retina,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale));

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
        [FromQuery, Required] string path,
        [FromQuery] string? preset = null,
        [FromQuery] string? bands = null,
        [FromQuery] string? formula = null,
        [FromQuery] string? bandFilter = null,
        [FromQuery] string? colormap = null,
        [FromQuery] string? rescale = null)
    {
        EnsureValidPath(path);

        var res = await _raster.ExportRasterAsync(orgSlug, dsSlug, path,
            new RasterVisualization(preset, bands, formula, bandFilter, colormap, rescale));

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
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        EnsureValidPaths(request.Paths);

        var json = await _processing.ValidateMergeMultispectralAsync(orgSlug, dsSlug, request.Paths);
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
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        EnsureValidPaths(request.Paths);

        var data = await _processing.PreviewMergeMultispectralAsync(orgSlug, dsSlug, request.Paths,
            request.PreviewBands, request.ThumbSize ?? 512);
        return File(data, "image/webp", "preview.webp");
    }

    /// <summary>Merge single-band rasters into a multi-band COG.</summary>
    [HttpPost("merge-multispectral", Name = nameof(ObjectsController) + "." + nameof(MergeMultispectral))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MergeMultispectral(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MergeMultispectralRequestDto request)
    {
        EnsureValidPaths(request.Paths, request.OutputPath);

        await _processing.MergeMultispectralAsync(orgSlug, dsSlug, request.Paths, request.OutputPath);
        return Ok(new { ok = true });
    }

    /// <summary>Validate that source and reference rasters are compatible for alignment.</summary>
    [HttpPost("align/validate",
        Name = nameof(ObjectsController) + "." + nameof(ValidateAlignRaster))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateAlignRaster(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] AlignRasterRequestDto request)
    {
        EnsureValidPaths([request.SourcePath, request.ReferencePath]);

        var json = await _processing.ValidateAlignRasterAsync(orgSlug, dsSlug,
            request.SourcePath, request.ReferencePath);
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
        [FromQuery, Required] string path)
    {
        EnsureValidPath(path);

        var json = await _raster.GetRasterValueInfoAsync(orgSlug, dsSlug, path);
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
        [FromQuery, Required] string path,
        [FromQuery, Required] int x,
        [FromQuery, Required] int y)
    {
        EnsureValidPath(path);

        var json = await _raster.GetRasterPointValueAsync(orgSlug, dsSlug, path, x, y);
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
        [FromQuery, Required] string path,
        [FromQuery, Required] int x0,
        [FromQuery, Required] int y0,
        [FromQuery, Required] int x1,
        [FromQuery, Required] int y1)
    {
        EnsureValidPath(path);

        var json = await _raster.GetRasterAreaStatsAsync(orgSlug, dsSlug, path, x0, y0, x1, y1);
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
        [FromBody, Required] RasterProfileRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        var json = await _raster.GetRasterProfileAsync(orgSlug, dsSlug, request.Path,
            request.LineString, request.Samples ?? 256);
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
        [FromBody, Required] ContourRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");

        EnsureValidPath(request.Path);

        var json = await _raster.GenerateContoursAsync(orgSlug, dsSlug, request.Path,
            new ContourOptions(request.Interval, request.Count, request.BaseOffset, request.MinElev, request.MaxElev,
                request.SimplifyTolerance, request.BandIndex));
        return Content(json, "application/json");
    }

    #endregion

    #region MaskBorders

    /// <summary>Check if a masked version of the file already exists.</summary>
    [HttpPost("mask-borders/check", Name = nameof(ObjectsController) + "." + nameof(CheckMaskBorders))]
    [ProducesResponseType(typeof(MaskBordersCheckResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckMaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request)
    {
        EnsureValidPath(request.Path);

        var result = await _processing.CheckMaskedFileExistsAsync(orgSlug, dsSlug, request.Path);
        return Ok(result);
    }

    /// <summary>Start masking orthophoto borders to make them transparent.</summary>
    [HttpPost("mask-borders", Name = nameof(ObjectsController) + "." + nameof(MaskBorders))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MaskBorders(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] MaskBordersRequestDto request)
    {
        // Early path traversal check
        EnsureValidPath(request.Path);

        await _processing.MaskBordersAsync(orgSlug, dsSlug, request.Path, request.Near, request.White);
        return Ok(new { ok = true });
    }

    #endregion
}
