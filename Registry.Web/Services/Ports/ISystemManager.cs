using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface ISystemManager
{
    public Task<CleanupBatchesResultDto> CleanupBatches();
    Task<CleanupDatasetResultDto> CleanupEmptyDatasets();
    string GetVersion();

    Task<IEnumerable<MigrateVisibilityEntryDTO>> MigrateVisibility();

    Task<BuildPendingStatusDto> GetBuildPendingStatus();

    Task<ImportResultDto> ImportDataset(ImportDatasetRequestDto request);
    Task<ImportResultDto> ImportOrganization(ImportOrganizationRequestDto request);

    /// <summary>
    /// Rescans the index of a specific dataset to update metadata
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="dsSlug">Dataset slug</param>
    /// <param name="types">Comma-separated list of entry types to rescan (e.g., "image,geoimage,pointcloud"), or null/empty for all</param>
    /// <param name="stopOnError">Whether to stop processing on first error</param>
    /// <returns>Rescan results</returns>
    Task<RescanResultDto> RescanDatasetIndex(string orgSlug, string dsSlug, string? types = null, bool stopOnError = true);
}