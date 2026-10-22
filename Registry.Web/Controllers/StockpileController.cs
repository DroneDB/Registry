#nullable enable
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
/// Stockpiles on an elevation raster: the volume over a polygon, and the footprints found around a click or
/// over the whole raster.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug)]
[Produces("application/json")]
public class StockpileController : ControllerBaseEx
{
    private readonly IRasterAnalysisService _raster;

    public StockpileController(IRasterAnalysisService raster)
    {
        _raster = raster;
    }


    #region Stockpile Volume

    /// <summary>
    /// Calculate cut/fill/net stockpile volume over a polygon on a DEM raster.
    /// </summary>
    [HttpPost("stockpile/calculate", Name = nameof(ObjectsController) + "." + nameof(CalculateStockpileVolume))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CalculateStockpileVolume(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] VolumeCalculationRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");


        var json = await _raster.CalculateVolumeAsync(orgSlug, dsSlug, request.Path.ToString(),
            request.Polygon, request.BaseMethod, request.FlatElevation);

        return Content(json, "application/json");
    }

    /// <summary>
    /// Auto-detect a stockpile footprint starting from a click on the raster.
    /// </summary>
    [HttpPost("stockpile/detect", Name = nameof(ObjectsController) + "." + nameof(DetectStockpile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DetectStockpile(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] StockpileDetectionRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");


        if (!request.Lat.HasValue || !request.Lon.HasValue)
            throw new BadRequestException("lat and lon are required");

        var json = await _raster.DetectStockpileAsync(orgSlug, dsSlug, request.Path.ToString(),
            request.Lat.Value, request.Lon.Value, request.Radius ?? 50.0, request.Sensitivity ?? 0.5f);
        return Content(json, "application/json");
    }

    /// <summary>
    /// Auto-detect all stockpile footprints in a DEM (full-raster scan).
    /// </summary>
    [HttpPost("stockpile/detect-all", Name = nameof(ObjectsController) + "." + nameof(DetectAllStockpiles))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DetectAllStockpiles(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] StockpileBatchDetectionRequestDto request)
    {
        if (request == null)
            throw new BadRequestException("Request body is required");


        var json = await _raster.DetectAllStockpilesAsync(orgSlug, dsSlug, request.Path.ToString(),
            request.Sensitivity ?? 0.5f, request.MinAreaM2 ?? 5.0, request.MaxResults ?? 50);
        return Content(json, "application/json");
    }

    #endregion
}
