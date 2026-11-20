using System.Net;
using System.Net.Sockets;

namespace Registry.Web.Utilities;

/// <summary>
/// The one rule for addresses that name no host of the public internet: this machine, the local network and ranges
/// that are reserved or special-purpose. Both questions the server asks about an address use it, so the answers agree:
/// whether an import may connect to it (<c>SsrfGuard</c>) and whether it can be the public address of the deployment
/// (<c>PublicHost</c>).
/// </summary>
public static class ReservedAddresses
{
    /// <summary>
    /// Whether <paramref name="address"/> is reserved: loopback, unspecified, private, link-local (the cloud metadata
    /// address 169.254.169.254 is in it), shared address space, benchmarking, multicast, broadcast or otherwise reserved.
    /// An IPv4-mapped IPv6 address counts as its IPv4 form, and an IPv6 address that carries an IPv4 one (the
    /// deprecated IPv4-compatible form, NAT64, 6to4, Teredo) is judged by the IPv4 address it carries, so a range that
    /// is refused as IPv4 cannot be reached through its IPv6 spelling.
    /// </summary>
    public static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? IsNonPublicV4(bytes) : IsNonPublicV6(address, bytes);
    }

    private static bool IsNonPublicV4(byte[] b) =>
        b[0] == 0                                   // 0.0.0.0/8, "this network"
        || b[0] == 10                               // 10.0.0.0/8
        || b[0] == 127                              // 127.0.0.0/8, loopback
        || (b[0] == 100 && (b[1] & 0xC0) == 64)     // 100.64.0.0/10, shared address space
        || (b[0] == 169 && b[1] == 254)             // 169.254.0.0/16, link-local and the cloud metadata address
        || (b[0] == 172 && (b[1] & 0xF0) == 16)     // 172.16.0.0/12
        || (b[0] == 192 && b[1] == 168)             // 192.168.0.0/16
        || (b[0] == 198 && (b[1] & 0xFE) == 18)     // 198.18.0.0/15, benchmarking
        || b[0] >= 224;                             // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, broadcast

    private static bool IsNonPublicV6(IPAddress address, byte[] b)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any))
            return true;

        // ::/96, the deprecated IPv4-compatible form, and anything unassigned next to :: and ::1
        if (IsZero(b, 0, 12))
            return true;

        return (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)      // fe80::/10, link-local
               || (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)   // fec0::/10, site-local (deprecated)
               || (b[0] & 0xFE) == 0xFC                     // fc00::/7, unique local (the AWS IPv6 metadata address is in it)
               || b[0] == 0xFF                              // ff00::/8, multicast
               || EmbedsNonPublicV4(b);
    }

    // NAT64 (64:ff9b::/96) carries the IPv4 address in its last 32 bits, 6to4 (2002::/16) in bits 16..47, Teredo
    // (2001::/32) the server in bits 32..63 and the client (inverted) in the last 32 bits
    private static bool EmbedsNonPublicV4(byte[] b)
    {
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && IsZero(b, 4, 8))
            return IsNonPublicV4(Slice(b, 12, invert: false));

        if (b[0] == 0x20 && b[1] == 0x02)
            return IsNonPublicV4(Slice(b, 2, invert: false));

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
            return IsNonPublicV4(Slice(b, 4, invert: false)) || IsNonPublicV4(Slice(b, 12, invert: true));

        return false;
    }

    private static byte[] Slice(byte[] bytes, int start, bool invert)
    {
        var v4 = new byte[4];
        for (var i = 0; i < 4; i++)
            v4[i] = invert ? (byte)~bytes[start + i] : bytes[start + i];
        return v4;
    }

    private static bool IsZero(byte[] bytes, int start, int count)
    {
        for (var i = start; i < start + count; i++)
            if (bytes[i] != 0)
                return false;
        return true;
    }
}
