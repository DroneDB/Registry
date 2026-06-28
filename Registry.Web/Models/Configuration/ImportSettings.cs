#nullable enable
using System;
using System.Linq;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// Configuration for the Import Dataset feature. Bound from the <c>AppSettings:Import</c> section.
/// Canonical definition for all import settings (see Import Dataset plan section 5.8).
/// </summary>
public class ImportSettings
{
    /// <summary>Maximum total bytes that can be imported in a single task (0 = unlimited).</summary>
    public long MaxImportSizeBytes { get; set; } = 0;

    /// <summary>
    /// Allowed source types. When null or empty, all registered sources are allowed.
    /// v1 values: "registry", "archive-url".
    /// </summary>
    public string[]? AllowedSourceTypes { get; set; }

    /// <summary>Allow outbound connections to private/loopback/link-local addresses (SSRF guard).</summary>
    public bool SsrfAllowPrivateNetworks { get; set; } = false;

    /// <summary>Hostnames explicitly exempt from SSRF blocking.</summary>
    public string[] SsrfAllowedHosts { get; set; } = [];

    /// <summary>
    /// Maximum number of HTTP redirects to follow during import (default 5). Every redirect hop is
    /// re-validated by the SSRF guard at connect time, so this only bounds redirect-chain length.
    /// </summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Connection/authentication timeout in seconds per source (default 30).</summary>
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>Total transfer timeout in seconds per import task (default 3600 = 1 h).</summary>
    public int TransferTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Number of files downloaded in parallel when importing from a remote registry (default 4).
    /// Kept conservative to avoid tripping the remote's rate limiter (HTTP 429); lower it further
    /// (e.g. 1-2) for hosts with aggressive throttling.
    /// </summary>
    public int RegistryDownloadConcurrency { get; set; } = 4;

    /// <summary>
    /// Maximum retry attempts per file download against a remote registry (default 6). Retries use
    /// exponential backoff with jitter and honor the remote's <c>Retry-After</c> header on HTTP 429.
    /// </summary>
    public int RegistryDownloadMaxRetries { get; set; } = 6;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="sourceType"/> is permitted by <see cref="AllowedSourceTypes"/>.
    /// A null or empty allow-list permits every registered source type. This is the single source of
    /// truth for the allow-list policy, enforced both at the web layer and on the worker.
    /// </summary>
    /// <param name="sourceType">The import source type identifier (e.g. <c>registry</c>, <c>archive-url</c>).</param>
    /// <returns><c>true</c> if the source type is allowed; otherwise <c>false</c>.</returns>
    public bool IsSourceTypeAllowed(string sourceType)
        => AllowedSourceTypes is null
           || AllowedSourceTypes.Length == 0
           || AllowedSourceTypes.Contains(sourceType, StringComparer.OrdinalIgnoreCase);
}
