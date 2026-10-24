#nullable enable
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.RemoteImport;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// Imports from another Registry (RQ-12, out of <see cref="SystemController"/>): the import runs as a background
/// job; these endpoints queue it, report its state and result, and cancel it. Admin only.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class RemoteImportController : ControllerBaseEx
{
    private readonly IRemoteImportService _imports;

    public RemoteImportController(IRemoteImportService imports) => _imports = imports;

    /// <summary>
    /// Queues the import of a dataset from another Registry instance.
    /// </summary>
    /// <param name="request">The source Registry, dataset and credentials, the destination and the mode.</param>
    /// <returns>The import job and its status URL (also the <c>Location</c> header).</returns>
    [HttpPost("import-dataset", Name = SystemRoutes.Prefix + nameof(ImportDataset))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(RemoteImportJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RemoteImportJobDto>> ImportDataset([FromBody] ImportDatasetRequestDto request, CancellationToken ct)
    {
        var job = await _imports.StartAsync(request, ct);
        return Accepted(job.StatusUrl, job);
    }

    /// <summary>
    /// Queues the import of an organization, with all its datasets, from another Registry instance.
    /// </summary>
    /// <param name="request">The source Registry, organization and credentials, the destination and the mode.</param>
    /// <returns>The import job and its status URL (also the <c>Location</c> header).</returns>
    [HttpPost("import-organization", Name = SystemRoutes.Prefix + nameof(ImportOrganization))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(RemoteImportJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RemoteImportJobDto>> ImportOrganization([FromBody] ImportOrganizationRequestDto request, CancellationToken ct)
    {
        var job = await _imports.StartAsync(request, ct);
        return Accepted(job.StatusUrl, job);
    }

    /// <summary>
    /// The state of an import job and, once it succeeded, its result (imported datasets, errors, statistics).
    /// </summary>
    /// <param name="jobId">The id returned when the import was queued.</param>
    [HttpGet("imports/{jobId}", Name = SystemRoutes.Prefix + nameof(GetImportStatus))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<RemoteImportStatusDto> GetImportStatus([Required] string jobId) => _imports.GetStatus(jobId);

    /// <summary>
    /// Cancels an import job: a queued import does not start, a running one stops.
    /// </summary>
    /// <param name="jobId">The id returned when the import was queued.</param>
    [HttpDelete("imports/{jobId}", Name = SystemRoutes.Prefix + nameof(CancelImport))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult CancelImport([Required] string jobId)
    {
        _imports.Cancel(jobId);
        return NoContent();
    }
}
