using System.Threading.Tasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Builds;

/// <summary>The requester of a job host: builds queued without an origin run as the service.</summary>
public sealed class ServiceBuildRequester : IBuildRequester
{
    public Task<string?> UserIdAsync() => Task.FromResult<string?>(null);
}

/// <summary>The requester of the web host: builds queued without an origin run as the signed-in user, if any.</summary>
public sealed class CurrentUserBuildRequester(IAuthManager authManager) : IBuildRequester
{
    public async Task<string?> UserIdAsync() => (await authManager.GetCurrentUser())?.Id;
}
