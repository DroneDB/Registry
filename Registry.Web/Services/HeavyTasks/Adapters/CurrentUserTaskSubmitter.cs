using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Submits a heavy task in the name of the current user: the request's user id and caller principal come
/// from the current request, so the callers that submit on a user's behalf (the task API, the dataset and file
/// imports) do not each assemble the <see cref="HeavyTaskSubmitRequest"/>. The caller has already checked access.
/// </summary>
public sealed class CurrentUserTaskSubmitter
{
    private readonly IHeavyTaskRunner _runner;
    private readonly IAuthManager _authManager;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserTaskSubmitter(IHeavyTaskRunner runner, IAuthManager authManager,
        IHttpContextAccessor httpContextAccessor)
    {
        _runner = runner;
        _authManager = authManager;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Validates, plans and enqueues <paramref name="toolId"/> for the dataset (see <see cref="IHeavyTaskRunner.SubmitAsync"/>).</summary>
    public async Task<HeavyTaskSubmitResult> SubmitAsync(string orgSlug, string dsSlug, string toolId,
        string? version, string? path, JsonElement parameters, bool force = false, CancellationToken ct = default)
    {
        var user = await _authManager.GetCurrentUser();

        var request = new HeavyTaskSubmitRequest(orgSlug, dsSlug, toolId, version, path, parameters, force,
            user?.Id, _httpContextAccessor.HttpContext?.User);

        return await _runner.SubmitAsync(request, ct);
    }
}
