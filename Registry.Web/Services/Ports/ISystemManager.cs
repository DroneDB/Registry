using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// System-level data maintenance (admin): expired upload batches, empty datasets, the visibility migration.
/// </summary>
public interface ISystemManager
{
    /// <summary>Removes the committed upload batches and the expired ones (with their files).</summary>
    Task<CleanupBatchesResultDto> CleanupBatches(CancellationToken ct = default);

    /// <summary>Removes the datasets whose index is empty.</summary>
    Task<CleanupDatasetResultDto> CleanupEmptyDatasets(CancellationToken ct = default);

    /// <summary>Gives a visibility to the datasets that still have only the legacy public flag.</summary>
    Task<IEnumerable<MigrateVisibilityEntryDTO>> MigrateVisibility(CancellationToken ct = default);
}
