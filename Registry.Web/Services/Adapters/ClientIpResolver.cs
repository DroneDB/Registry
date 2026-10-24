#nullable enable
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The address of the client that made a request, for per-client limits (rate limiting, download
/// concurrency). Behind a reverse proxy this is the address the proxy forwarded, but only when the
/// TCP peer is one of the configured <c>KnownProxies</c> or inside <c>KnownNetworks</c>: the forwarded headers are applied by
/// <c>UseForwardedHeaders</c> at the start of the pipeline, never read here, so a client cannot
/// spoof its address with an <c>X-Forwarded-For</c> header.
/// </summary>
public sealed class ClientIpResolver
{
    private const string AnonymousKeyPrefix = "ip:";
    private const string UnknownAddress = "unknown";

    public IPAddress? Resolve(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// The per-client partition key of anonymous requests: <c>ip:</c> followed by the client address
    /// (<c>ip:unknown</c> when the address is not known).
    /// </summary>
    public string AnonymousKey(HttpContext context) =>
        AnonymousKeyPrefix + (Resolve(context)?.ToString() ?? UnknownAddress);
}
