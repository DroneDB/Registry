#nullable enable
using System.Net;
using Microsoft.AspNetCore.Http;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IClientIpResolver"/>: the connection's remote address after
/// <c>UseForwardedHeaders</c> applied the headers of a known proxy. IPv4 addresses mapped to IPv6
/// (dual-mode sockets) are reported as IPv4, so one client gets one key whatever the socket family.
/// </summary>
public sealed class ClientIpResolver : IClientIpResolver
{
    private const string AnonymousKeyPrefix = "ip:";
    private const string UnknownAddress = "unknown";

    public IPAddress? Resolve(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    public string AnonymousKey(HttpContext context) =>
        AnonymousKeyPrefix + (Resolve(context)?.ToString() ?? UnknownAddress);
}
