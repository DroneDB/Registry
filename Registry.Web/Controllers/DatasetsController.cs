#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing datasets within organizations.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetsPrefix)]
[Produces("application/json")]
public class DatasetsController : ControllerBaseEx
{
    private readonly IDatasetsManager _datasetsManager;
    private readonly ShareManager _shareManager;
    private readonly RescanManager _rescanManager;

    public DatasetsController(IDatasetsManager datasetsManager, ShareManager shareManager, RescanManager rescanManager)
    {
        _datasetsManager = datasetsManager;
        _shareManager = shareManager;
        _rescanManager = rescanManager;
    }

    /// <summary>
    /// Gets all batches for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>A list of batches.</returns>
    [HttpGet(RoutesHelper.DatasetSlug + "/batches", Name = nameof(GetBatches))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<BatchDto>>> GetBatches(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        var lst = await _shareManager.ListBatches(orgSlug, dsSlug, ct: ct);

        return Answer(lst);

    }

    /// <summary>
    /// Gets all datasets for a specific organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>A list of datasets.</returns>
    [HttpGet(Name = nameof(DatasetsController) + "." + nameof(GetAll))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<DatasetDto>>> GetAll([FromRoute, Required] string orgSlug, [FromQuery] bool includeThumbnailCheck = false,
        [FromQuery] int? skip = null, [FromQuery] int? take = null, [FromQuery] string q = null,
        [FromQuery] string sort = null, [FromQuery] bool desc = false, [FromQuery] string slugs = null, CancellationToken ct = default)
    {
        // Without paging parameters: the whole list, as before
        if (skip == null && take == null && q == null && sort == null && slugs == null)
            return Answer(await _datasetsManager.List(orgSlug, includeThumbnailCheck, ct: ct));

        var slugList = slugs?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (items, total) = await _datasetsManager.ListPage(orgSlug,
            new DatasetListQuery(skip ?? 0, take, q, sort, desc, slugList), includeThumbnailCheck, ct: ct);
        Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Answer<IEnumerable<DatasetDto>>(items);
    }

    /// <summary>
    /// Gets the entry information for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The dataset entry information.</returns>
    [HttpGet(RoutesHelper.DatasetSlug, Name = nameof(DatasetsController) + "." + nameof(Get))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntryDto[]>> Get(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        return await _datasetsManager.GetEntry(orgSlug, dsSlug, ct: ct);
    }

    /// <summary>
    /// Gets extended information for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The dataset extended information.</returns>
    [HttpGet(RoutesHelper.DatasetSlug + "/ex", Name = nameof(DatasetsController) + "." + nameof(GetEx))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DatasetDto>> GetEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        return await _datasetsManager.Get(orgSlug, dsSlug, ct: ct);
    }

    /// <summary>
    /// Gets the stamp (checksum and entries metadata) for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The dataset stamp.</returns>
    [HttpGet(RoutesHelper.DatasetSlug + "/stamp", Name = nameof(DatasetsController) + "." + nameof(GetStamp))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StampDto>> GetStamp(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        return await _datasetsManager.GetStamp(orgSlug, dsSlug, ct: ct);
    }

    /// <summary>
    /// Creates a new dataset in the specified organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dataset">The dataset creation data.</param>
    /// <returns>The newly created dataset.</returns>
    [HttpPost(Name = nameof(DatasetsController) + "." + nameof(Create))]
    [ProducesResponseType(typeof(DatasetDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DatasetDto>> Create(
        [FromRoute, Required] string orgSlug,
        [FromForm, Required] DatasetNewDto dataset, CancellationToken ct)
    {
        var newDs = await _datasetsManager.AddNew(orgSlug, dataset, ct: ct);
        return CreatedAtRoute(
            nameof(DatasetsController) + "." + nameof(Get),
            new { orgSlug = orgSlug, dsSlug = newDs.Slug }, newDs);
    }

    /// <summary>
    /// Renames a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The current dataset slug.</param>
    /// <param name="newSlug">The new dataset slug.</param>
    /// <returns>The renamed dataset.</returns>
    [HttpPost(RoutesHelper.DatasetSlug + "/rename", Name = nameof(Rename))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DatasetDto>> Rename(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "slug"), Required] string newSlug, CancellationToken ct)
    {
        await _datasetsManager.Rename(orgSlug, dsSlug, newSlug, ct: ct);

        return await _datasetsManager.Get(orgSlug, newSlug, ct: ct);
    }

    /// <summary>
    /// Changes the attributes of a dataset. Obsolete alias of the visibility change (<c>PUT</c> of the dataset, or
    /// the <c>visibility</c> meta): <c>{"public": true}</c> makes it Public, <c>false</c> Private, no <c>public</c>
    /// changes nothing.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="rawAttributes">The attributes as JSON string.</param>
    /// <returns>The updated attributes.</returns>
    [HttpPost(RoutesHelper.DatasetSlug + "/chattr", Name = nameof(ChangeAttributes))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Dictionary<string, object>>> ChangeAttributes(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "attrs")] string rawAttributes, CancellationToken ct)
    {
        var attributes = string.IsNullOrWhiteSpace(rawAttributes) ?
            new AttributesDto() :
            ApiJson.Deserialize<AttributesDto>(rawAttributes);

        var res = await _datasetsManager.ChangeAttributes(orgSlug, dsSlug, attributes, ct: ct);

        return res;
    }

    /// <summary>
    /// Updates a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="dataset">The dataset update data.</param>
    /// <returns>No content on success.</returns>
    [HttpPut(RoutesHelper.DatasetSlug, Name = nameof(DatasetsController) + "." + nameof(Update))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] DatasetEditDto dataset, CancellationToken ct)
    {
        await _datasetsManager.Edit(orgSlug, dsSlug, dataset, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Deletes a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete(RoutesHelper.DatasetSlug, Name = nameof(DatasetsController) + "." + nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        await _datasetsManager.Delete(orgSlug, dsSlug, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Rescans the index of a dataset to update metadata.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="types">Optional comma-separated list of entry types to rescan (e.g., "image,geoimage,pointcloud"). If not specified, all types are rescanned.</param>
    /// <param name="stopOnError">Whether to stop processing on first error. Default is true.</param>
    /// <param name="extensions">Optional comma-separated list of file extensions to rescan (e.g., "dwg,dxf"). An entry must match both filters. With a filter only the rescanned entries are rebuilt.</param>
    /// <returns>The rescan result with processed entries and statistics.</returns>
    [HttpPost(RoutesHelper.DatasetSlug + "/rescan", Name = nameof(DatasetsController) + "." + nameof(RescanIndex))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RescanResultDto>> RescanIndex(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string? types = null,
        [FromQuery] bool stopOnError = true,
        [FromQuery] string? extensions = null, CancellationToken ct = default)
    {
        return await _rescanManager.RescanDatasetIndex(orgSlug, dsSlug,
            new RescanOptions(types, extensions, stopOnError), ct: ct);
    }

}