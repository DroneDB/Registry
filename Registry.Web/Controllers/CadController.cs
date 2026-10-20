#nullable enable
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>CAD drawings of a dataset (DDB-12).</summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug + "/cad")]
[Produces("application/json")]
public class CadController(ICadCrsManager crsManager) : ControllerBaseEx
{
    /// <summary>
    /// Sets the coordinate system of a CAD drawing by writing its .prj: an authority code
    /// ("EPSG:32632"), WKT or PROJ string, or "local" to keep the drawing in its own coordinates.
    /// Delete the .prj to go back to the drawing's own GEODATA.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">The drawing (.dxf or .dwg).</param>
    /// <param name="crs">The coordinate system.</param>
    /// <returns>The .prj entry.</returns>
    [HttpPost("crs", Name = nameof(CadController) + "." + nameof(SetCrs))]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetCrs(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string path,
        [FromForm, Required] string crs) =>
        Ok(await crsManager.SetAsync(orgSlug, dsSlug, path, crs));
}
