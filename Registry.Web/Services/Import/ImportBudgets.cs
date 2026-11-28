using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Import;

/// <summary>
/// The budget of a file that an import writes to disk: the size cap, then the disk head-room of the volume it is
/// written to (the configured safety margin plus libddb's free-disk floor, <see cref="VolumeHeadroom"/>). Each file, and
/// each attempt of a retried download, takes its own.
/// </summary>
public sealed class ImportBudgets(VolumeHeadroom headroom, IOptions<ProcessingPlatformSettings> platformSettings)
{
    /// <summary>The budget of a write into <paramref name="destinationFolder"/>.</summary>
    /// <param name="maxBytes">The size cap; 0 (or less) sets none.</param>
    /// <param name="destinationFolder">A folder on the volume written to.</param>
    /// <param name="maxBytesMessage">The text of the failure when the cap is reached; null: the text of the archive size limit.</param>
    public ExtractionBudget For(long maxBytes, string destinationFolder, string? maxBytesMessage = null) =>
        headroom.CreateBudget(maxBytes, destinationFolder, platformSettings.Value.DiskSafetyMarginBytes, maxBytesMessage);
}
