#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Import;

/// <summary>
/// Prevents Server-Side Request Forgery by rejecting requests to private/loopback/link-local
/// addresses unless the deployment explicitly allows them. Blocks the cloud instance metadata
/// endpoint 169.254.169.254. Augments (does not replace) format-only URL validation such as
/// <c>SystemManager.ValidateRegistryUrl</c>.
/// </summary>
public sealed class SsrfGuard
{
    private static readonly IPAddress MetadataEndpoint = IPAddress.Parse("169.254.169.254");

    private readonly ImportSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="SsrfGuard"/> class.
    /// </summary>
    /// <param name="settings">Import settings carrying the SSRF allow-list and toggles.</param>
    public SsrfGuard(ImportSettings settings) => _settings = settings;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if the hostname resolves to a blocked address.
    /// </summary>
    /// <param name="host">The hostname (or IP literal) to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the host has been validated.</returns>
    public async Task AssertAllowedAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A host is required.");

        if (IsBypassed(host))
            return;

        IPAddress[] addresses;
        try
        {
            // If the host is already an IP literal, GetHostAddressesAsync returns it as-is.
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"Cannot resolve host '{host}'.");
        }

        AssertAddressesAllowed(host, addresses);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and asserts every resolved address is allowed, returning the
    /// addresses so the caller can connect to exactly those IPs. Dialing the validated IPs (instead of
    /// re-resolving) closes the DNS-rebinding window between this check and the socket connect; because
    /// it runs for every connection it also blocks redirects whose target resolves to a private address.
    /// </summary>
    /// <param name="host">The hostname (or IP literal) to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validated resolved addresses.</returns>
    public async Task<IPAddress[]> ResolveAndAssertAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A host is required.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException($"Cannot resolve host '{host}'.");
        }

        // Bypassed hosts still need resolution (the caller connects to the result) but skip blocking.
        if (!IsBypassed(host))
            AssertAddressesAllowed(host, addresses);

        return addresses;
    }

    private bool IsBypassed(string host) =>
        _settings.SsrfAllowPrivateNetworks ||
        (_settings.SsrfAllowedHosts is { Length: > 0 } allowed &&
         allowed.Contains(host, StringComparer.OrdinalIgnoreCase));

    private static void AssertAddressesAllowed(string host, IPAddress[] addresses)
    {
        foreach (var addr in addresses)
        {
            if (IsBlocked(addr))
                throw new ArgumentException(
                    $"Requests to private or reserved IP addresses are not allowed ('{host}' -> {addr}).");
        }
    }

    private static bool IsBlocked(IPAddress addr) =>
        IPAddress.IsLoopback(addr)
        || addr.IsIPv6LinkLocal
        || addr.IsIPv6SiteLocal
        || addr.Equals(MetadataEndpoint)
        || IsPrivateIPv4(addr);

    private static bool IsPrivateIPv4(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = addr.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254); // link-local / cloud metadata
    }
}
