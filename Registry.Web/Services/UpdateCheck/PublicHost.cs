#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using Registry.Web.Services.Import;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// Whether an address names a host that can be reached from the internet. The update check reports the public URL of a
/// deployment, so the address a request used on the local network or on the machine itself (<c>localhost</c>, a private
/// or link-local IP, a host name of one label such as <c>registry</c>) says nothing and is ignored.
/// </summary>
public static class PublicHost
{
    /// <summary>
    /// Whether the host of <paramref name="url"/> is public: an absolute http(s) URL whose host is a name of at least two
    /// labels (not <c>localhost</c>) or an IP address that is not loopback, private, link-local or reserved.
    /// </summary>
    public static bool IsPublic(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IsPublic(address);

        var host = uri.IdnHost.TrimEnd('.');
        return host.Contains('.', StringComparison.Ordinal) &&
               !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
               !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (SsrfGuard.IsBlocked(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast)
            return false;

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;

        // 0.0.0.0/8, shared address space 100.64.0.0/10, multicast and reserved 224.0.0.0/3
        var b = address.GetAddressBytes();
        return b[0] != 0 && !(b[0] == 100 && (b[1] & 0xC0) == 64) && b[0] < 224;
    }
}
