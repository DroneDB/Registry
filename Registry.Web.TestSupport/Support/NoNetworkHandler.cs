#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Test.Support;

/// <summary>
/// A message handler that never reaches the network: it fails every request the way an unreachable host does. A
/// <see cref="RegistryWebFactory"/> host uses it for the update check, so no test run sends anything to the real service.
/// </summary>
public sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("The network is not available in tests");
}
