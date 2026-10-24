#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Submits a heavy task in the name of the current user (RQ-12): the request's user id and caller principal come
/// from the current request, so the callers that submit on a user's behalf (the task API, the dataset and file
/// imports) do not each assemble the <see cref="HeavyTaskSubmitRequest"/>. The caller has already checked access.
/// </summary>
public interface ICurrentUserTaskSubmitter
{
    /// <summary>Validates, plans and enqueues <paramref name="toolId"/> for the dataset (see <see cref="IHeavyTaskRunner.SubmitAsync"/>).</summary>
    Task<HeavyTaskSubmitResult> SubmitAsync(string orgSlug, string dsSlug, string toolId, string? version,
        string? path, JsonElement parameters, bool force = false, CancellationToken ct = default);
}
