using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using IMetaManager = Registry.Web.Services.Ports.IMetaManager;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing metadata within datasets.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix + "/" +
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MetaDto>> Add(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        // Nullable so that an empty JSON string is not rejected as missing; an empty body is still rejected
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Disallow)] string? data,
        [FromQuery] string? path = null, CancellationToken ct = default)
    {
        var res = await _metaManager.Add(orgSlug, dsSlug, key, data, path, ct: ct);

        return res;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<ActionResult<MetaDto>> AddAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "key")] string? keyFromForm,
        [FromForm] string? data,
        [FromForm(Name = "path")] string? pathFromForm = null,
        [FromQuery(Name = "path")] string? pathFromQuery = null,
        [FromQuery(Name = "key")] string? keyFromQuery = null, CancellationToken ct = default)
    {
        // C# magics, precedence to form parameter
        var path = pathFromForm ?? pathFromQuery;
        var key = keyFromForm ?? keyFromQuery;

        var res = await _metaManager.Add(orgSlug, dsSlug, key, data, path, ct: ct);

        return res;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MetaDto>> Set(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        // Nullable so that an empty JSON string is not rejected as missing; an empty body is still rejected
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Disallow)] string? data,
        [FromQuery] string? path = null, CancellationToken ct = default)
    {
        var res = await _metaManager.Set(orgSlug, dsSlug, key, data, path, ct: ct);

        return res;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<ActionResult<MetaDto>> SetAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "key")] string? keyFromForm,
        [FromForm] string? data,
        [FromForm(Name = "path")] string? pathFromForm = null,
        [FromQuery(Name = "path")] string? pathFromQuery = null,
        [FromQuery(Name = "key")] string? keyFromQuery = null, CancellationToken ct = default)
    {
        // C# magics, precedence to form parameter
        var path = pathFromForm ?? pathFromQuery;
        var key = keyFromForm ?? keyFromQuery;

        var res = await _metaManager.Set(orgSlug, dsSlug, key, data, path, ct: ct);

        return res;
    }

    /// <summary>
    /// Removes a specific metadata entry by ID.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="id">The metadata entry ID to remove.</param>
    /// <returns>The count of removed entries.</returns>
    [HttpDelete("remove/{id}", Name = nameof(MetaController) + "." + nameof(Remove))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RemoveResponse>> Remove(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var res = await _metaManager.Remove(orgSlug, dsSlug, id, ct: ct);

        return new RemoveResponse(res);
    }

    /// <summary>
    /// Removes a specific metadata entry by ID using form data (alternative endpoint).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="id">The metadata entry ID to remove.</param>
    /// <returns>The count of removed entries.</returns>
    [HttpPost("remove", Name = nameof(MetaController) + "." + nameof(RemoveAlt))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<ActionResult<RemoveResponse>> RemoveAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string id, CancellationToken ct)
    {
        var res = await _metaManager.Remove(orgSlug, dsSlug, id, ct: ct);

        return new RemoveResponse(res);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RemoveResponse>> Unset(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        [FromQuery] string? path = null, CancellationToken ct = default)
    {
        var res = await _metaManager.Unset(orgSlug, dsSlug, key, path, ct: ct);

        return new RemoveResponse(res);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<ActionResult<RemoveResponse>> UnsetAlt(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "key")] string? keyFromForm,
        [FromForm(Name = "path")] string? pathFromForm = null,
        [FromQuery(Name = "path")] string? pathFromQuery = null,
        [FromQuery(Name = "key")] string? keyFromQuery = null, CancellationToken ct = default)
    {
        // C# magics, precedence to form parameter
        var path = pathFromForm ?? pathFromQuery;
        var key = keyFromForm ?? keyFromQuery;

        var res = await _metaManager.Unset(orgSlug, dsSlug, key, path, ct: ct);

        return new RemoveResponse(res);
    }

    /// <summary>
    /// Lists all metadata keys for the dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="path">Optional path within the dataset.</param>
    /// <returns>A list of metadata keys with their counts.</returns>
    [HttpGet("list", Name = nameof(MetaController) + "." + nameof(List))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<MetaListItemDto>>> List(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string? path = null, CancellationToken ct = default)
    {
        var res = await _metaManager.List(orgSlug, dsSlug, path, ct: ct);

        return Answer(res);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JsonElement>> Get(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string key,
        [FromQuery] string? path = null, CancellationToken ct = default)
    {
        return await _metaManager.Get(orgSlug, dsSlug, key, path, ct: ct);
    }

    /// <summary>
    /// Dumps all metadata entries, optionally filtered by IDs.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="ids">Optional comma-separated list of metadata IDs to include.</param>
    /// <returns>A list of all metadata entries.</returns>
    [HttpPost("dump", Name = nameof(MetaController) + "." + nameof(Dump))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<ActionResult<IEnumerable<MetaDumpDto>>> Dump(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] string? ids = null, CancellationToken ct = default)
    {
        var res = await _metaManager.Dump(orgSlug, dsSlug, ids, ct: ct);

        return Answer(res);
    }
}