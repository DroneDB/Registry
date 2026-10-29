using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// System (non-user) submit of any heavy tool, for background jobs that span datasets.
/// Unlike <see cref="IHeavyTaskRunner"/> it needs no request-scoped services, so it
/// activates on processing nodes too. Bypasses quota and dedup; the tool still
/// validates the request.
/// </summary>
public interface ISystemTaskSubmitter
{
    /// <param name="ddb">The dataset index, for the tool validation.</param>
    /// <param name="userId">The user the task is attributed to, or null for the system.</param>
    /// <returns>The task (Hangfire job) id.</returns>
    Task<string> SubmitAsync(string toolId, string orgSlug, string dsSlug, IDDB ddb, string? path,
        JsonElement prms, string? userId = null, CancellationToken ct = default);
}
