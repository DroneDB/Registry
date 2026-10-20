using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// System-level operations interface (cleanup, version, migration, import).
/// </summary>
public interface ISystemManager
{
    public Task<CleanupBatchesResultDto> CleanupBatches();
    Task<CleanupDatasetResultDto> CleanupEmptyDatasets();
    string GetVersion();

    Task<IEnumerable<MigrateVisibilityEntryDTO>> MigrateVisibility();

    /// <summary>
    /// Renames organizations and datasets whose slug fails validation (e.g. "--" from the
    /// pre-collapsing slug generator) to their ToSlug form, moving organization folders.
    /// With dryRun only the plan is returned.
    /// </summary>
    Task<FixInvalidSlugsResultDto> FixInvalidSlugs(bool dryRun = true);

    Task<BuildPendingStatusDto> GetBuildPendingStatus();

    Task<ImportResultDto> ImportDataset(ImportDatasetRequestDto request);
    Task<ImportResultDto> ImportOrganization(ImportOrganizationRequestDto request);

    /// <summary>
    /// Removes terminal (Succeeded/Failed/Deleted) JobIndex records older than the configured retention period.
    /// </summary>
    /// <param name="retentionDays">Optional override for retention days. Uses AppSettings.JobIndexRetentionDays when null.</param>
    /// <returns>Cleanup result with number of records deleted.</returns>
    Task<CleanupJobIndicesResultDto> CleanupJobIndices(int? retentionDays = null);

    /// <summary>
    /// Generates a global report of all organizations and datasets. Admin only.
    /// </summary>
    /// <returns>Global report containing user, organizations, datasets, and file statistics.</returns>
    Task<GlobalReportDto> GetGlobalReport();

    /// <summary>
    /// Cleans up build artifacts and stale entries on one dataset, all datasets in an
    /// organization, or every dataset in the system. Admin only.
    /// When both slugs are null the operation is enqueued as a background job.
    /// </summary>
    Task<CleanupBuildResultDto> CleanupBuild(CleanupBuildRequestDto request);
}