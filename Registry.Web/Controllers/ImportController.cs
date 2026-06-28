#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// REST surface for importing a remote dataset (another Registry instance or a downloadable archive)
/// into a newly created dataset. All routes live under <c>/orgs/{org}/import</c>.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/import")]
[Produces("application/json")]
public class ImportController : ControllerBaseEx
{
    private readonly IImportManager _importManager;
    private readonly ILogger<ImportController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportController"/> class.
    /// </summary>
    /// <param name="importManager">The import manager.</param>
    /// <param name="logger">The logger.</param>
    public ImportController(IImportManager importManager, ILogger<ImportController> logger)
    {
        _importManager = importManager;
        _logger = logger;
    }

    /// <summary>
    /// Lists the import source types enabled on this server.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>The available source type identifiers.</returns>
    [HttpGet("sources", Name = nameof(GetImportSources))]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public IActionResult GetImportSources([FromRoute, Required] string orgSlug)
    {
        try
        {
            return Ok(_importManager.GetAvailableSourceTypes());
        }
        catch (Exception ex)
        {
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Verifies (probes) an import source without creating anything.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="body">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    [HttpPost("verify", Name = nameof(VerifyImport))]
    [ProducesResponseType(typeof(ImportVerifyResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyImport(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] VerifyImportRequestDto body,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _importManager.VerifyAsync(orgSlug, body, ct));
        }
        catch (Exception ex)
        {
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Creates an empty dataset and starts importing the source into it.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="body">The create request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created dataset and the tracking task id.</returns>
    [HttpPost(Name = nameof(CreateImport))]
    [ProducesResponseType(typeof(ImportCreateResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateImport(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] CreateImportRequestDto body,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _importManager.CreateAsync(orgSlug, body, ct));
        }
        catch (Exception ex)
        {
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Browses the remote structure of an import source (lists organizations or datasets) without
    /// creating or modifying anything. Only the <c>registry</c> source type supports this operation.
    /// </summary>
    /// <param name="orgSlug">The organization slug (used for access control only).</param>
    /// <param name="body">The browse request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of organizations or datasets available at the remote source.</returns>
    [HttpPost("browse", Name = nameof(BrowseImport))]
    [ProducesResponseType(typeof(ImportBrowseResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BrowseImport(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] BrowseImportRequestDto body,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _importManager.BrowseAsync(orgSlug, body, ct));
        }
        catch (Exception ex)
        {
            return ExceptionResult(ex);
        }
    }
}
