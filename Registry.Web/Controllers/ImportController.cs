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
[Route(RoutesHelper.OrganizationPrefix + "/import")]
[Produces("application/json")]
public class ImportController : ControllerBaseEx
{
    private readonly IImportManager _importManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportController"/> class.
    /// </summary>
    /// <param name="importManager">The import manager.</param>
    public ImportController(IImportManager importManager)
    {
        _importManager = importManager;
    }

    /// <summary>
    /// Lists the import source types enabled on this server, to a caller who may import into the
    /// organization (write access).
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>The available source type identifiers.</returns>
    [HttpGet("sources", Name = nameof(GetImportSources))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<string>>> GetImportSources([FromRoute, Required] string orgSlug, CancellationToken ct)
    {
        return Answer<IEnumerable<string>>(await _importManager.GetAvailableSourceTypes(orgSlug, ct: ct));
    }

    /// <summary>
    /// Verifies (probes) an import source without creating anything.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="body">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    [HttpPost("verify", Name = nameof(VerifyImport))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImportVerifyResultDto>> VerifyImport(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] VerifyImportRequestDto body,
        CancellationToken ct)
    {
        return await _importManager.VerifyAsync(orgSlug, body, ct);
    }

    /// <summary>
    /// Creates an empty dataset and starts importing the source into it.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="body">The create request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created dataset and the tracking task id.</returns>
    [HttpPost(Name = nameof(CreateImport))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImportCreateResultDto>> CreateImport(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] CreateImportRequestDto body,
        CancellationToken ct)
    {
        return await _importManager.CreateAsync(orgSlug, body, ct);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportBrowseResultDto>> BrowseImport(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] BrowseImportRequestDto body,
        CancellationToken ct)
    {
        return await _importManager.BrowseAsync(orgSlug, body, ct);
    }
}
