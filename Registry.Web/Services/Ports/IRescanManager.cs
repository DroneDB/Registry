#nullable enable
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Index rescans: re-read entries to refresh their metadata, then rebuild what changed
/// (see <see cref="Adapters.RescanRebuild"/>). The system rescan across datasets is
/// <see cref="Maintenance.IBuildMaintenance.RescanAsync"/>.
/// </summary>
public interface IRescanManager
{
    /// <summary>
    /// Rescans the index of one dataset synchronously and queues the rebuilds.
    /// Admins and users with write access to the dataset only.
    /// </summary>
    /// <param name="options">Types and extensions to rescan (null for all) and whether to stop at the first error.</param>
    Task<RescanResultDto> RescanDatasetIndex(string orgSlug, string dsSlug, RescanOptions? options = null);
}
