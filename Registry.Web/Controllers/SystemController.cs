using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Configuration;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// System administration: the version, the data maintenance (upload batches, empty datasets, visibility, slugs),
/// moving datasets and merging organizations, the global report and the configuration. The build
/// maintenance, the remote import, the processing nodes, the features and the update check have their own controllers (same routes,
/// same route names: <see cref="SystemRoutes"/>).
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class SystemController : ControllerBaseEx
{
    private readonly SystemManager _systemManager;
    private readonly DatasetSlugRepair _slugs;
    private readonly GlobalReportBuilder _report;
    private readonly IDatasetsManager _datasets;
    private readonly IOrganizationsManager _organizations;
    private readonly ConfigurationDataBuilder _configuration;

    public SystemController(SystemManager systemManager, DatasetSlugRepair slugs, GlobalReportBuilder report,
        IDatasetsManager datasets, IOrganizationsManager organizations, ConfigurationDataBuilder configuration)
    {
        _systemManager = systemManager;
        _slugs = slugs;
        _report = report;
        _datasets = datasets;
        _organizations = organizations;
        _configuration = configuration;
    }

    /// <summary>
    /// Gets the current version of the Registry system.
    /// </summary>
    /// <returns>The version string.</returns>
    [HttpGet("version", Name = SystemRoutes.Prefix + nameof(GetVersion))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public ActionResult<string?> GetVersion()
    {
        return RegistryVersion.Current;
    }

    /// <summary>
    /// Cleans up expired or orphaned batches from the system.
    /// </summary>
    /// <returns>The cleanup result with removed batches and any errors encountered.</returns>
    [HttpPost("cleanupbatches", Name = SystemRoutes.Prefix + nameof(CleanupBatches))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CleanupBatchesResultDto>> CleanupBatches(CancellationToken ct)
    {
        return await _systemManager.CleanupBatches(ct: ct);
    }

    /// <summary>
    /// Cleans up empty datasets from the system.
    /// </summary>
    /// <returns>The cleanup result with removed datasets and any errors encountered.</returns>
    [HttpPost("cleanupdatasets", Name = SystemRoutes.Prefix + nameof(CleanupDatasets))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CleanupDatasetResultDto>> CleanupDatasets(CancellationToken ct)
    {
        return await _systemManager.CleanupEmptyDatasets(ct: ct);
    }

    /// <summary>
    /// Migrates dataset visibility settings from the legacy format to the new format.
    /// </summary>
    /// <returns>A list of migrated visibility entries.</returns>
    [HttpPost("migratevisibility", Name = SystemRoutes.Prefix + nameof(MigrateVisibility))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<MigrateVisibilityEntryDTO>>> MigrateVisibility(CancellationToken ct)
    {
        return Answer(await _systemManager.MigrateVisibility(ct: ct));
    }

    /// <summary>
    /// Renames organizations and datasets whose slug is invalid (e.g. contains "--", or has one character: the
    /// slug rules need two) to their ToSlug form (padded to the minimum length) and moves organization folders
    /// accordingly. Dry run by default: pass
    /// dryRun=false to apply. Entries with active jobs are skipped; run again once they finish.
    /// </summary>
    /// <param name="dryRun">When true (default) only returns the planned renames.</param>
    [HttpPost("fix-invalid-slugs", Name = SystemRoutes.Prefix + nameof(FixInvalidSlugs))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FixInvalidSlugsResultDto>> FixInvalidSlugs([FromQuery] bool dryRun = true, CancellationToken ct = default)
    {
        return await _slugs.FixInvalidSlugsAsync(dryRun, ct: ct);
    }

    /// <summary>
    /// Moves one or more datasets from one organization to another.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="request">The move request containing the source organization slug, dataset slugs, destination organization, and conflict resolution strategy.</param>
    /// <returns>Results of the move operation for each dataset.</returns>
    [HttpPost("move-datasets", Name = SystemRoutes.Prefix + nameof(MoveDatasets))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IEnumerable<MoveDatasetResultDto>>> MoveDatasets(
        [FromBody, Required] MoveDatasetDto request, CancellationToken ct)
    {
        var results = await _datasets.MoveToOrganization(
            request.SourceOrgSlug,
            request.DatasetSlugs,
            request.DestinationOrgSlug,
            request.ConflictResolution, ct: ct);

        return Answer(results);
    }

    /// <summary>
    /// Merges a source organization into a destination organization.
    /// All datasets from the source will be moved to the destination.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="request">The merge request containing destination organization and options.</param>
    /// <returns>Result of the merge operation.</returns>
    [HttpPost("merge-organizations", Name = SystemRoutes.Prefix + nameof(MergeOrganizations))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MergeOrganizationResultDto>> MergeOrganizations(
        [FromBody, Required] MergeOrganizationDto request, CancellationToken ct)
    {
        var result = await _organizations.Merge(
            request.SourceOrgSlug,
            request.DestinationOrgSlug,
            request.ConflictResolution,
            request.DeleteSourceOrganization, ct: ct);

        return result;
    }

    /// <summary>
    /// Generates a global report of all organizations and datasets. Admin only.
    /// </summary>
    /// <returns>Global report containing user, organizations, datasets, and file statistics.</returns>
    [HttpGet("report", Name = SystemRoutes.Prefix + nameof(GetGlobalReport))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<GlobalReportDto>> GetGlobalReport(CancellationToken ct)
    {
        return await _report.BuildAsync(ct: ct);
    }

    /// <summary>
    /// Gets all Registry configuration fields grouped by section, with default values,
    /// descriptions, and typed metadata for the admin configuration editor page.
    /// Sensitive values (secrets, passwords, tokens) are never exposed - only IsSet flags.
    /// </summary>
    /// <returns>ConfigurationDataDto with all sections and fields.</returns>
    [HttpGet("config", Name = SystemRoutes.Prefix + nameof(GetConfig))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public ActionResult<ConfigurationDataDto> GetConfig()
    {
        return _configuration.Build();
    }
}
