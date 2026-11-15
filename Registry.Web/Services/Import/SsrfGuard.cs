using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Import;

/// <summary>
/// Prevents Server-Side Request Forgery by rejecting requests to private/loopback/link-local
/// addresses unless the deployment explicitly allows them. Blocks the cloud instance metadata
/// endpoint 169.254.169.254. Augments (does not replace) format-only URL validation such as
/// <c>SystemManager.ValidateRegistryUrl</c>.
/// </summary>
public sealed class SsrfGuard
{
    /// <summary>
    /// What the user is told when the address of a source is local or private (FR-UPL-19): why, and what to do, without
    /// the technical detail (the resolved address stays in the log). <paramref name="host"/> is the host as the user typed it.
    /// </summary>
    public static string BlockedHostMessage(string host) =>
        $"This server cannot import from local or private network addresses ({host}). Use the public address of the registry.";

    private readonly ImportSettings _settings;
    private readonly ILogger<SsrfGuard>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SsrfGuard"/> class.
    /// </summary>
    /// <param name="settings">Import settings carrying the SSRF allow-list and toggles.</param>
    /// <param name="logger">Where a refused address is logged, with the address it resolved to.</param>
    public SsrfGuard(ImportSettings settings, ILogger<SsrfGuard>? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

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

    private void AssertAddressesAllowed(string host, IPAddress[] addresses)
    {
        foreach (var addr in addresses)
        {
            if (!IsBlocked(addr))
                continue;

            // The host and the address are for the log; the user gets the plain message
            _logger?.LogWarning("Import refused: '{Host}' resolves to the private or reserved address {Address}", host, addr);
            throw new ArgumentException(BlockedHostMessage(host));
        }
    }

    /// <summary>
    /// Whether <paramref name="addr"/> is a local, private or reserved address, the cloud metadata address
    /// included (<see cref="ReservedAddresses"/>).
    /// </summary>
    internal static bool IsBlocked(IPAddress addr) => ReservedAddresses.IsNonPublic(addr);
}
