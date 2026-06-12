#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Admin-only global task dashboard manager (spec §B.1.2). Lists tasks across all
/// users and datasets with server-side paging and tool/state/user filters.
/// </summary>
public interface IAdminTasksManager
{
    Task<AdminTaskListDto> ListAsync(string? toolId, string? state, string? userId,
        int skip, int take, CancellationToken ct = default);
}
