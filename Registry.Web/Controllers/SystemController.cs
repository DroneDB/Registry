using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for system administration and maintenance operations.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class SystemController : ControllerBaseEx
{
    private readonly ISystemManager _systemManager;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IOrganizationsManager _organizationsManager;
    private readonly ILogger<SystemController> _logger;

    public SystemController(
        ISystemManager systemManager,
        IDatasetsManager datasetsManager,
        IOrganizationsManager organizationsManager,
        ILogger<SystemController> logger)
    {
        _systemManager = systemManager;
        _datasetsManager = datasetsManager;
        _organizationsManager = organizationsManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current version of the Registry system.
    /// </summary>
    /// <returns>The version string.</returns>
    [HttpGet("version", Name = nameof(SystemController) + "." + nameof(GetVersion))]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult GetVersion()
    {
        try
        {
            _logger.LogDebug("System controller GetVersion()");

            return Ok(_systemManager.GetVersion());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetVersion()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Cleans up expired or orphaned batches from the system.
    /// </summary>
    /// <returns>The cleanup result with removed batches and any errors encountered.</returns>
    [HttpPost("cleanupbatches", Name = nameof(SystemController) + "." + nameof(CleanupBatches))]
    [ProducesResponseType(typeof(CleanupBatchesResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CleanupBatches()
    {
        try
        {
            _logger.LogDebug("System controller CleanupBatches()");

            return Ok(await _systemManager.CleanupBatches());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupBatches()");

            return ExceptionResult(ex);
        }
    }


    /// <summary>
    /// Cleans up empty datasets from the system.
    /// </summary>
    /// <returns>The cleanup result with removed datasets and any errors encountered.</returns>
    [HttpPost("cleanupdatasets", Name = nameof(SystemController) + "." + nameof(CleanupDatasets))]
    [ProducesResponseType(typeof(CleanupDatasetResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CleanupDatasets()
    {
        try
        {
            _logger.LogDebug("System controller CleanupDatasets()");

            return Ok(await _systemManager.CleanupEmptyDatasets());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupDatasets()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Migrates dataset visibility settings from the legacy format to the new format.
    /// </summary>
    /// <returns>A list of migrated visibility entries.</returns>
    [HttpPost("migratevisibility", Name = nameof(SystemController) + "." + nameof(MigrateVisibility))]
    [ProducesResponseType(typeof(IEnumerable<MigrateVisibilityEntryDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MigrateVisibility()
    {
        try
        {
            _logger.LogDebug("System controller MigrateVisibility()");

            return Ok(await _systemManager.MigrateVisibility());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupDatasets()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the status of the build pending background job.
    /// </summary>
    /// <returns>The current status of the build pending job including metrics and next scheduled run.</returns>
    [HttpGet("build-pending-status", Name = nameof(SystemController) + "." + nameof(GetBuildPendingStatus))]
    [ProducesResponseType(typeof(BuildPendingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetBuildPendingStatus()
    {
        try
        {
            _logger.LogDebug("System controller GetBuildPendingStatus()");

            return Ok(await _systemManager.GetBuildPendingStatus());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetBuildPendingStatus()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Imports a dataset from another Registry instance.
    /// </summary>
    /// <param name="request">The import request containing source and destination information.</param>
    /// <returns>The import result with imported items, errors, and statistics.</returns>
    [HttpPost("import-dataset", Name = nameof(SystemController) + "." + nameof(ImportDataset))]
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportDataset([FromBody] ImportDatasetRequestDto request)
    {
        try
        {
            _logger.LogDebug("System controller ImportDataset('{SourceOrg}/{SourceDs}' from '{SourceUrl}')",
                request.SourceOrganization, request.SourceDataset, request.SourceRegistryUrl);

            return Ok(await _systemManager.ImportDataset(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller ImportDataset('{SourceOrg}/{SourceDs}' from '{SourceUrl}')",
                request?.SourceOrganization, request?.SourceDataset, request?.SourceRegistryUrl);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Imports an entire organization with all its datasets from another Registry instance.
    /// </summary>
    /// <param name="request">The import request containing source and destination organization information.</param>
    /// <returns>The import result with imported items, errors, and statistics.</returns>
    [HttpPost("import-organization", Name = nameof(SystemController) + "." + nameof(ImportOrganization))]
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportOrganization([FromBody] ImportOrganizationRequestDto request)
    {
        try
        {
            _logger.LogDebug("System controller ImportOrganization('{SourceOrg}' from '{SourceUrl}')",
                request.SourceOrganization, request.SourceRegistryUrl);

            return Ok(await _systemManager.ImportOrganization(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller ImportOrganization('{SourceOrg}' from '{SourceUrl}')",
                request?.SourceOrganization, request?.SourceRegistryUrl);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Moves one or more datasets from one organization to another.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="sourceOrgSlug">The source organization slug.</param>
    /// <param name="request">The move request containing dataset slugs and destination organization.</param>
    /// <returns>Results of the move operation for each dataset.</returns>
    [HttpPost("move-datasets", Name = nameof(SystemController) + "." + nameof(MoveDatasets))]
    [ProducesResponseType(typeof(IEnumerable<MoveDatasetResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MoveDatasets(
        [FromBody, Required] MoveDatasetDto request)
    {
        try
        {
            _logger.LogDebug("System controller MoveDatasets('{SourceOrgSlug}', datasets: [{DatasetSlugs}], destination: '{DestOrgSlug}', conflictResolution: {ConflictResolution})",
                request?.SourceOrgSlug,
                string.Join(", ", request?.DatasetSlugs ?? []),
                request?.DestinationOrgSlug,
                request?.ConflictResolution);

            var results = await _datasetsManager.MoveToOrganization(
                request?.SourceOrgSlug,
                request?.DatasetSlugs,
                request?.DestinationOrgSlug,
                request!.ConflictResolution);

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller MoveDatasets");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Merges a source organization into a destination organization.
    /// All datasets from the source will be moved to the destination.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="request">The merge request containing destination organization and options.</param>
    /// <returns>Result of the merge operation.</returns>
    [HttpPost("merge-organizations", Name = nameof(SystemController) + "." + nameof(MergeOrganizations))]
    [ProducesResponseType(typeof(MergeOrganizationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MergeOrganizations(
        [FromBody, Required] MergeOrganizationDto request)
    {
        try
        {
            _logger.LogDebug("System controller MergeOrganizations('{SourceOrgSlug}' -> '{DestOrgSlug}', conflictResolution: {ConflictResolution}, deleteSource: {DeleteSource})",
                request?.SourceOrgSlug,
                request?.DestinationOrgSlug,
                request?.ConflictResolution,
                request?.DeleteSourceOrganization);

            var result = await _organizationsManager.Merge(
                request?.SourceOrgSlug,
                request?.DestinationOrgSlug,
                request!.ConflictResolution,
                request!.DeleteSourceOrganization);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller MergeOrganizations");
            return ExceptionResult(ex);
        }
    }

}