using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Common.Model;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing datasets within organizations.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix)]
[Produces("application/json")]
public class DatasetsController : ControllerBaseEx
{
    private readonly IDatasetsManager _datasetsManager;
    private readonly IShareManager _shareManager;
    private readonly ILogger<DatasetsController> _logger;

    public DatasetsController(IDatasetsManager datasetsManager, IShareManager shareManager, ILogger<DatasetsController> logger)
    {
        _datasetsManager = datasetsManager;
        _shareManager = shareManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets all batches for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>A list of batches.</returns>
    [HttpGet(RoutesHelper.DatasetSlug + "/batches", Name = nameof(GetBatches))]
    [ProducesResponseType(typeof(IEnumerable<BatchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatches(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller GetBatches('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            var lst = await _shareManager.ListBatches(orgSlug, dsSlug);

            return Ok(lst);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller GetBatches('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets all datasets for a specific organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>A list of datasets.</returns>
    [HttpGet(Name = nameof(DatasetsController) + "." + nameof(GetAll))]
    [ProducesResponseType(typeof(IEnumerable<DatasetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAll([FromRoute, Required] string orgSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller GetAll('{OrgSlug}')", orgSlug);
            return Ok(await _datasetsManager.List(orgSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller GetAll('{OrgSlug}')", orgSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the entry information for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The dataset entry information.</returns>
    [HttpGet(RoutesHelper.DatasetSlug, Name = nameof(DatasetsController) + "." + nameof(Get))]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller Get('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            return Ok(await _datasetsManager.GetEntry(orgSlug, dsSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller Get('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets extended information for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The dataset extended information.</returns>
    [HttpGet(RoutesHelper.DatasetSlug + "/ex", Name = nameof(DatasetsController) + "." + nameof(GetEx))]
    [ProducesResponseType(typeof(DatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEx(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller GetEx('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            return Ok(await _datasetsManager.Get(orgSlug, dsSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller GetEx('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the stamp (checksum and entries metadata) for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>The dataset stamp.</returns>
    [HttpGet(RoutesHelper.DatasetSlug + "/stamp", Name = nameof(DatasetsController) + "." + nameof(GetStamp))]
    [ProducesResponseType(typeof(Stamp), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStamp(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller GetStamp('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            return Ok(await _datasetsManager.GetStamp(orgSlug, dsSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller GetStamp('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Creates a new dataset in the specified organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dataset">The dataset creation data.</param>
    /// <returns>The newly created dataset.</returns>
    [HttpPost(Name = nameof(DatasetsController) + "." + nameof(Create))]
    [ProducesResponseType(typeof(DatasetDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromRoute, Required] string orgSlug,
        [FromForm, Required] DatasetNewDto dataset)
    {
        try
        {
            _logger.LogDebug("Dataset controller Create('{OrgSlug}', '{DatasetSlug}')", orgSlug, dataset?.Slug);

            var newDs = await _datasetsManager.AddNew(orgSlug, dataset);
            return CreatedAtRoute(
                nameof(DatasetsController) + "." + nameof(Get),
                new { orgSlug = orgSlug, dsSlug = newDs.Slug }, newDs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller Create('{OrgSlug}', '{DatasetSlug}')", orgSlug, dataset?.Slug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Renames a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The current dataset slug.</param>
    /// <param name="newSlug">The new dataset slug.</param>
    /// <returns>The renamed dataset.</returns>
    [HttpPost(RoutesHelper.DatasetSlug + "/rename", Name = nameof(Rename))]
    [ProducesResponseType(typeof(DatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Rename(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "slug"), Required] string newSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller Rename('{OrgSlug}', '{DsSlug}', '{NewSlug}')", orgSlug, dsSlug, newSlug);

            await _datasetsManager.Rename(orgSlug, dsSlug, newSlug);

            return Ok(await _datasetsManager.Get(orgSlug, newSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller Rename('{OrgSlug}', '{Dslug}', '{NewSlug}')", orgSlug, dsSlug, newSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Changes the attributes (visibility) of a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="rawAttributes">The attributes as JSON string.</param>
    /// <returns>The updated attributes.</returns>
    [HttpPost(RoutesHelper.DatasetSlug + "/chattr", Name = nameof(ChangeAttributes))]
    [ProducesResponseType(typeof(AttributesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeAttributes(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm(Name = "attrs")] string rawAttributes)
    {
        try
        {

            var attributes = string.IsNullOrWhiteSpace(rawAttributes) ?
                new AttributesDto() :
                JsonConvert.DeserializeObject<AttributesDto>(rawAttributes);

            _logger.LogDebug("Dataset controller ChangeAttributes('{OrgSlug}', '{DsSlug}', {RawAttributes}')", orgSlug, dsSlug, rawAttributes);

            var res = await _datasetsManager.ChangeAttributes(orgSlug, dsSlug, attributes);

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller ChangeAttributes('{OrgSlug}', '{DsSlug}', '{RawAttributes}')", orgSlug, dsSlug, rawAttributes);
            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm] DatasetEditDto dataset)
    {
        try
        {
            _logger.LogDebug("Dataset controller Update('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            await _datasetsManager.Edit(orgSlug, dsSlug, dataset);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller Update('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Deletes a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete(RoutesHelper.DatasetSlug, Name = nameof(DatasetsController) + "." + nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Dataset controller Delete('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

            await _datasetsManager.Delete(orgSlug, dsSlug);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Dataset controller Delete('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);
            return ExceptionResult(ex);
        }
    }
}