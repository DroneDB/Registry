#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Features;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Processing;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// Build and index maintenance across datasets (RQ-12, out of <see cref="SystemController"/>): the build cleanup,
/// the system rescan, the pending-build sweep status, the job index retention, the artifact completeness check.
/// Admin only.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class BuildMaintenanceController : ControllerBaseEx
{
    private readonly IBuildMaintenance _maintenance;

    public BuildMaintenanceController(IBuildMaintenance maintenance) => _maintenance = maintenance;

    /// <summary>
    /// Cleans up build artifacts and stale entries on a single dataset, all datasets in
    /// an organization, or every dataset in the system. Admin only.
    /// When more than one dataset is targeted, the cleanup is enqueued as background jobs.
    /// </summary>
    /// <param name="request">
    /// Selects the cleanup scope. When both slugs are null the cleanup runs across all
    /// organizations. When only OrganizationSlug is set the cleanup runs across all
    /// datasets in that organization. When both are set the cleanup targets a single
    /// dataset and runs synchronously.
    /// </param>
    /// <returns>The cleanup result with removed entries/builds (sync) or job id (async).</returns>
    [HttpPost("cleanup", Name = SystemRoutes.Prefix + nameof(Cleanup))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(CleanupBuildResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CleanupBuildResultDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Cleanup([FromBody] CleanupBuildRequestDto? request)
    {
        var result = await _maintenance.CleanupBuildAsync(request);
        return result.Async ? Accepted(result) : Ok(result);
    }

    /// <summary>
    /// Rescans the index entries that match a type and/or extension filter across all
    /// organizations, one organization or one dataset, e.g. to re-read every DWG drawing
    /// after DroneDB learns the format. Admin only.
    /// A background job submits one rescan-index task per dataset that holds matching
    /// entries; each task re-reads those entries and rebuilds only them.
    /// </summary>
    /// <param name="request">
    /// Scope (OrganizationSlug, DatasetSlug; both null for every dataset) and filters
    /// (Types, Extensions; an entry must match both, at least one is required).
    /// </param>
    /// <returns>The id of the background job that submits the dataset tasks.</returns>
    [HttpPost("rescan", Name = SystemRoutes.Prefix + nameof(Rescan))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(RescanJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Rescan([FromBody] RescanRequestDto request)
    {
        return Accepted(await _maintenance.RescanAsync(request ?? new RescanRequestDto()));
    }

    /// <summary>
    /// Gets the status of the build pending background job.
    /// </summary>
    /// <returns>The current status of the build pending job including metrics and next scheduled run.</returns>
    [HttpGet("build-pending-status", Name = SystemRoutes.Prefix + nameof(GetBuildPendingStatus))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(BuildPendingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public IActionResult GetBuildPendingStatus([FromServices] BuildPendingService pending)
    {
        return Ok(pending.GetStatus());
    }

    /// <summary>
    /// Removes old terminal (Succeeded/Failed/Deleted) records from the JobIndices table.
    /// Useful for manual cleanup when the table has grown too large.
    /// </summary>
    /// <param name="retentionDays">Optional override for retention period in days. Uses the configured default when omitted.</param>
    /// <returns>Cleanup result with the number of records deleted.</returns>
    [HttpPost("cleanup-jobindices", Name = SystemRoutes.Prefix + nameof(CleanupJobIndices))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(CleanupJobIndicesResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CleanupJobIndices([FromQuery] int? retentionDays = null)
    {
        return Ok(await _maintenance.CleanupJobIndicesAsync(retentionDays));
    }

    /// <summary>
    /// Triggers an immediate, one-off run of the artifact completeness checker.
    /// Scans every entry in every dataset and enqueues a rebuild for any
    /// buildable entry whose build output is missing or empty.
    /// </summary>
    /// <returns>The Hangfire job id of the enqueued scan.</returns>
    [HttpPost("check-artifact-completeness",
        Name = SystemRoutes.Prefix + nameof(CheckArtifactCompleteness))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public IActionResult CheckArtifactCompleteness([FromServices] IBackgroundJobsProcessor jobs)
    {
        var jobId = jobs.Enqueue<ArtifactCompletenessCheckerService>(
            s => s.CheckAndQueueAsync(null, CancellationToken.None));

        return Accepted(new { JobId = jobId });
    }
}
