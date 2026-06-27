#nullable enable
using System;
using System.Net.Http;
using System.Net.Sockets;

namespace Registry.Web.Services.Import;

/// <summary>
/// Factory for the SSRF-hardened <see cref="SocketsHttpHandler"/>. The handler's
/// <see cref="SocketsHttpHandler.ConnectCallback"/> resolves the host and validates the resolved
/// address through <see cref="SsrfGuard"/> at the exact moment the socket is opened, then dials those
/// validated IPs. Because the callback runs for the initial request AND for every auto-followed
/// redirect hop, it blocks both:
/// <list type="bullet">
/// <item>redirects (3xx) whose target resolves to a private/reserved address;</item>
/// <item>DNS rebinding, by removing the second name lookup between validation and connect.</item>
/// </list>
/// Redirect chains are capped via <see cref="SocketsHttpHandler.MaxAutomaticRedirections"/>.
/// (<see cref="SocketsHttpHandler"/> is sealed, so this is a configuring factory, not a subclass.)
/// </summary>
public static class SsrfHttpHandler
{
    /// <summary>Name of the SSRF-hardened HTTP client registered with <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "import-ssrf";

    /// <summary>
    /// Builds a <see cref="SocketsHttpHandler"/> that validates every connection's resolved IP through
    /// <paramref name="ssrfGuard"/> before connecting.
    /// </summary>
    /// <param name="ssrfGuard">The guard that validates each resolved address before connecting.</param>
    /// <param name="maxRedirects">Maximum number of redirects to follow (clamped to at least 1).</param>
    /// <returns>The configured handler.</returns>
    public static SocketsHttpHandler Create(SsrfGuard ssrfGuard, int maxRedirects = 5)
    {
        ArgumentNullException.ThrowIfNull(ssrfGuard);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = maxRedirects < 1 ? 1 : maxRedirects
        };

        handler.ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            // Resolve + validate, then connect to exactly the validated addresses.
            var addresses = await ssrfGuard.ResolveAndAssertAsync(host, ct);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        return handler;
    }
}
