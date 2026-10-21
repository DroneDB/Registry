using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using IMetaManager = Registry.Web.Services.Ports.IMetaManager;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing metadata within datasets.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" +
       RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" +
       RoutesHelper.DatasetSlug + "/" +
       RoutesHelper.MetaRadix)]
[Produces("application/json")]
public class MetaController : ControllerBaseEx
{
    private readonly IMetaManager _metaManager;

    public MetaController(IMetaManager metaManager)
    {
        _metaManager = metaManager;
    }

    /// <summary>
    /// Adds metadata with the specified key.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="data">The metadata value as JSON string.</param>
    /// <param name="path">Optional path within the dataset.</param>
    /// <returns>The created metadata entry.</returns>
    [HttpPost("add/{key}", Name = nameof(MetaController) + "." + nameof(Add))]
    [ProducesResponseType(typeof(MetaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        [FromBody] string data,
        [FromQuery] string path = null)
    {
        var res = await _metaManager.Add(orgSlug, dsSlug, key, data, path);

        return Ok(res);
    }

    /// <summary>
    /// Adds metadata using form data (alternative endpoint).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="keyFromForm">The metadata key from form.</param>
    /// <param name="data">The metadata value.</param>
    /// <param name="pathFromForm">Optional path from form.</param>
    /// <param name="pathFromQuery">Optional path from query.</param>
    /// <param name="keyFromQuery">Optional key from query.</param>
    /// <returns>The created metadata entry.</returns>
    [HttpPost("add", Name = nameof(MetaController) + "." + nameof(AddAlt))]
    [ProducesResponseType(typeof(MetaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> AddAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "key")] string keyFromForm,
        [FromForm] string data,
        [FromForm(Name = "path")] string pathFromForm = null,
        [FromQuery(Name = "path")] string pathFromQuery = null,
        [FromQuery(Name = "key")] string keyFromQuery = null)
    {
        // C# magics, precedence to form parameter
        var path = pathFromForm ?? pathFromQuery;
        var key = keyFromForm ?? keyFromQuery;

        var res = await _metaManager.Add(orgSlug, dsSlug, key, data, path);

        return Ok(res);
    }

    /// <summary>
    /// Sets (replaces) metadata with the specified key.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="data">The metadata value as JSON string.</param>
    /// <param name="path">Optional path within the dataset.</param>
    /// <returns>The updated metadata entry.</returns>
    [HttpPost("set/{key}", Name = nameof(MetaController) + "." + nameof(Set))]
    [ProducesResponseType(typeof(MetaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Set(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        [FromBody] string data,
        [FromQuery] string path = null)
    {
        var res = await _metaManager.Set(orgSlug, dsSlug, key, data, path);

        return Ok(res);
    }

    /// <summary>
    /// Sets (replaces) metadata using form data (alternative endpoint).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="keyFromForm">The metadata key from form.</param>
    /// <param name="data">The metadata value.</param>
    /// <param name="pathFromForm">Optional path from form.</param>
    /// <param name="pathFromQuery">Optional path from query.</param>
    /// <param name="keyFromQuery">Optional key from query.</param>
    /// <returns>The updated metadata entry.</returns>
    [HttpPost("set", Name = nameof(MetaController) + "." + nameof(SetAlt))]
    [ProducesResponseType(typeof(MetaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> SetAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "key")] string keyFromForm,
        [FromForm] string data,
        [FromForm(Name = "path")] string pathFromForm = null,
        [FromQuery(Name = "path")] string pathFromQuery = null,
        [FromQuery(Name = "key")] string keyFromQuery = null)
    {
        // C# magics, precedence to form parameter
        var path = pathFromForm ?? pathFromQuery;
        var key = keyFromForm ?? keyFromQuery;

        var res = await _metaManager.Set(orgSlug, dsSlug, key, data, path);

        return Ok(res);
    }

    /// <summary>
    /// Removes a specific metadata entry by ID.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="id">The metadata entry ID to remove.</param>
    /// <returns>The count of removed entries.</returns>
    [HttpDelete("remove/{id}", Name = nameof(MetaController) + "." + nameof(Remove))]
    [ProducesResponseType(typeof(RemoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id)
    {
        var res = await _metaManager.Remove(orgSlug, dsSlug, id);

        return Ok(new RemoveResponse(res));
    }

    /// <summary>
    /// Removes a specific metadata entry by ID using form data (alternative endpoint).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="id">The metadata entry ID to remove.</param>
    /// <returns>The count of removed entries.</returns>
    [HttpPost("remove", Name = nameof(MetaController) + "." + nameof(RemoveAlt))]
    [ProducesResponseType(typeof(RemoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> RemoveAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string id)
    {
        var res = await _metaManager.Remove(orgSlug, dsSlug, id);

        return Ok(new RemoveResponse(res));
    }

    /// <summary>
    /// Removes all metadata entries with the specified key.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="key">The metadata key to unset.</param>
    /// <param name="path">Optional path within the dataset.</param>
    /// <returns>The count of removed entries.</returns>
    [HttpDelete("unset/{key}", Name = nameof(MetaController) + "." + nameof(Unset))]
    [ProducesResponseType(typeof(RemoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unset(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        [FromQuery] string path = null)
    {
        var res = await _metaManager.Unset(orgSlug, dsSlug, key, path);

        return Ok(new RemoveResponse(res));
    }

    /// <summary>
    /// Removes all metadata entries with the specified key using form data (alternative endpoint).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="keyFromForm">The metadata key from form.</param>
    /// <param name="pathFromForm">Optional path from form.</param>
    /// <param name="pathFromQuery">Optional path from query.</param>
    /// <param name="keyFromQuery">Optional key from query.</param>
    /// <returns>The count of removed entries.</returns>
    [HttpPost("unset", Name = nameof(MetaController) + "." + nameof(UnsetAlt))]
    [ProducesResponseType(typeof(RemoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> UnsetAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "key")] string keyFromForm,
        [FromForm(Name = "path")] string pathFromForm = null,
        [FromQuery(Name = "path")] string pathFromQuery = null,
        [FromQuery(Name = "key")] string keyFromQuery = null)
    {
        // C# magics, precedence to form parameter
        var path = pathFromForm ?? pathFromQuery;
        var key = keyFromForm ?? keyFromQuery;

        var res = await _metaManager.Unset(orgSlug, dsSlug, key, path);

        return Ok(new RemoveResponse(res));
    }

    /// <summary>
    /// Lists all metadata keys for the dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">Optional path within the dataset.</param>
    /// <returns>A list of metadata keys with their counts.</returns>
    [HttpGet("list", Name = nameof(MetaController) + "." + nameof(List))]
    [ProducesResponseType(typeof(IEnumerable<MetaListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string path = null)
    {
        var res = await _metaManager.List(orgSlug, dsSlug, path);

        return Ok(res);
    }

    /// <summary>
    /// Gets metadata values for the specified key.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="path">Optional path within the dataset.</param>
    /// <returns>The metadata values as JSON.</returns>
    [HttpGet("get/{key}", Name = nameof(MetaController) + "." + nameof(Get))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        [FromQuery] string path = null)
    {
        var res = await _metaManager.Get(orgSlug, dsSlug, key, path);

        return Ok(res);
    }

    /// <summary>
    /// Dumps all metadata entries, optionally filtered by IDs.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="ids">Optional comma-separated list of metadata IDs to include.</param>
    /// <returns>A list of all metadata entries.</returns>
    [HttpPost("dump", Name = nameof(MetaController) + "." + nameof(Dump))]
    [ProducesResponseType(typeof(IEnumerable<MetaDumpDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> Dump(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string ids = null)
    {
        var res = await _metaManager.Dump(orgSlug, dsSlug, ids);

        return Ok(res);
    }
}