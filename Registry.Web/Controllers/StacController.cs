using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for STAC (SpatioTemporal Asset Catalog) API endpoints.
/// Provides access to datasets in STAC format for interoperability with geospatial tools.
/// </summary>
[ApiController]
[Tags("STAC")]
[Produces("application/json")]
public class StacController : ControllerBaseEx
{
    private readonly IStacManager _stacManager;
    private readonly ILogger<StacController> _logger;

    public StacController(IStacManager stacManager,
        ILogger<StacController> logger)
    {
        _stacManager = stacManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the root STAC catalog containing links to all public datasets.
    /// </summary>
    /// <returns>The root STAC catalog with links to child collections.</returns>
    [HttpGet("/stac", Name = nameof(StacController) + "." + nameof(GetCatalog))]
    [ProducesResponseType(typeof(StacCatalogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCatalog()
    {
        try
        {
            _logger.LogDebug("Stac controller GetCatalog()");

            return Ok(await _stacManager.GetCatalog());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetCatalog()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets a STAC child resource (Collection or Item) for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="pathBase64">Optional Base64-encoded path to a specific item within the dataset.</param>
    /// <returns>A STAC Collection or Item depending on the path. Returns a Collection when no path is specified, or an Item when a specific asset path is provided.</returns>
    [HttpGet("/orgs/{orgSlug}/ds/{dsSlug}/stac/{pathBase64?}",
        Name = nameof(StacController) + "." + nameof(GetStacChild))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStacChild(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute] string pathBase64 = null)
    {
        try
        {
            _logger.LogDebug("Stac controller GetStacChild()");

            var path = pathBase64 != null ? Encoding.UTF8.GetString(Convert.FromBase64String(pathBase64)) : null;

            return Ok(await _stacManager.GetStacChild(orgSlug, dsSlug, path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetStacChild()");

            return ExceptionResult(ex);
        }
    }
}