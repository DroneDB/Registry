#nullable enable
using System;
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
/// REST surface for importing a single file from a URL into an existing dataset. All routes live under
/// <c>/orgs/{org}/ds/{ds}/import</c>. Distinct from <see cref="ImportController"/>, which imports a
/// whole remote dataset into a freshly created one.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix + "/import")]
[Produces("application/json")]
public class DatasetImportController : ControllerBaseEx
{
    private readonly IFileUrlImportManager _importManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatasetImportController"/> class.
    /// </summary>
    /// <param name="importManager">The single-file URL import manager.</param>
    public DatasetImportController(IFileUrlImportManager importManager)
    {
        _importManager = importManager;
    }

    /// <summary>
    /// Verifies (probes) a file URL without downloading it: reachability, size, derived file name and
    /// the outcome of the deny-list / size checks.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="body">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    [HttpPost("verify-url", Name = nameof(VerifyUrlImport))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UrlImportVerifyResultDto>> VerifyUrlImport(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] UrlImportVerifyRequestDto body,
        CancellationToken ct)
    {
        return await _importManager.VerifyAsync(orgSlug, dsSlug, body, ct);
    }

    /// <summary>
    /// Submits the background task that downloads the file from the URL and adds it to the dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="body">The import request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tracking task id.</returns>
    [HttpPost("url", Name = nameof(ImportUrl))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UrlImportResultDto>> ImportUrl(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromBody, Required] UrlImportRequestDto body,
        CancellationToken ct)
    {
        return await _importManager.ImportAsync(orgSlug, dsSlug, body, ct);
    }
}
