#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Admin-only global task dashboard manager. Lists tasks across all
/// users and datasets with server-side paging and tool/state/user filters.
/// </summary>
public interface IAdminTasksManager
{
    Task<AdminTaskListDto> ListAsync(string? toolId, string? state, string? userId,
        int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Deletes a single terminal (Succeeded/Failed/Deleted) job from the JobIndex table
    /// and purges any associated artifacts. Admin-only.
    /// </summary>
    Task<bool> DeleteTaskAsync(string jobId, CancellationToken ct = default);
}
