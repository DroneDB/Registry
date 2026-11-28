using System.Net.Http;

namespace Registry.Web.Test.Support;

/// <summary>
/// Per-request helpers for the <see cref="RegistryWebFactory"/> test headers.
/// </summary>
public static class TestRequestExtensions
{
    /// <summary>
    /// Authenticates the request as <paramref name="userName"/> through <see cref="TestAuthHandler"/>.
    /// </summary>
    public static HttpRequestMessage AsUser(this HttpRequestMessage request, string userName,
        bool isAdmin = false)
    {
        request.Headers.Remove(TestAuthHandler.UserHeader);
        request.Headers.Add(TestAuthHandler.UserHeader, TestAuthHandler.HeaderValue(userName, isAdmin));
        return request;
    }

    /// <summary>
    /// Sets the emulated TCP peer address of the request (see <see cref="TestPeerIpStartupFilter"/>).
    /// </summary>
    public static HttpRequestMessage FromPeer(this HttpRequestMessage request, string ipAddress)
    {
        request.Headers.Remove(TestPeerIpStartupFilter.HeaderName);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, ipAddress);
        return request;
    }
}
