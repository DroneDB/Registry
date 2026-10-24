#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>Default <see cref="ICurrentUserTaskSubmitter"/>.</summary>
public sealed class CurrentUserTaskSubmitter : ICurrentUserTaskSubmitter
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

    public async Task<HeavyTaskSubmitResult> SubmitAsync(string orgSlug, string dsSlug, string toolId,
        string? version, string? path, JsonElement parameters, bool force = false, CancellationToken ct = default)
    {
        var user = await _authManager.GetCurrentUser();

        var request = new HeavyTaskSubmitRequest(orgSlug, dsSlug, toolId, version, path, parameters, force,
            user?.Id, _httpContextAccessor.HttpContext?.User);

        return await _runner.SubmitAsync(request, ct);
    }
}
