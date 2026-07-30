#nullable enable
using System;
using System.IO;
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
    /// Maximum size (bytes) of a single file imported from a URL via the <c>import-file</c> tool
    /// (0 = fall back to <see cref="MaxImportSizeBytes"/>). Enforced up-front against the reported
    /// <c>Content-Length</c> and again incrementally while streaming.
    /// </summary>
    public long MaxFileImportSizeBytes { get; set; } = 0;

    /// <summary>
    /// Minimum sustained download speed (bytes/second) for a single-file URL import. If the average
    /// throughput stays below this value for <see cref="LowSpeedGraceSeconds"/> seconds the download
    /// is aborted. Set to <c>0</c> to disable the low-speed guard. Default 1 KiB/s.
    /// </summary>
    public long MinDownloadSpeedBytesPerSec { get; set; } = 1024;

    /// <summary>
    /// Window (seconds) over which the average speed must stay below
    /// <see cref="MinDownloadSpeedBytesPerSec"/> before a slow download is aborted. Also used as the
    /// hard stall timeout for a single read. Default 30.
    /// </summary>
    public int LowSpeedGraceSeconds { get; set; } = 30;

    /// <summary>
    /// File extensions (with or without the leading dot) rejected by ALL ingestion paths (single-file
    /// URL import, archive extraction and archive URL import) as a defense-in-depth deny-list against
    /// executables/scripts. Matching is case-insensitive. Ignored when <see cref="AllowedFileExtensions"/>
    /// is non-empty (the allow-list takes precedence).
    /// </summary>
    public string[] BlockedFileExtensions { get; set; } =
    [
        ".exe", ".dll", ".so", ".dylib", ".bat", ".cmd", ".com", ".msi", ".msix", ".appx",
        ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".sh", ".bash", ".zsh", ".ksh", ".csh", ".run", ".elf", ".scr", ".pif", ".cpl",
        ".gadget", ".jar", ".apk", ".app", ".deb", ".rpm", ".py", ".pyc", ".pyo", ".rb",
        ".pl", ".php", ".lnk", ".reg", ".command", ".workflow"
    ];

    /// <summary>
    /// File extensions (with or without the leading dot) that are the ONLY ones accepted by ALL
    /// ingestion paths (single-file URL import, archive extraction and archive URL import). When this
    /// list is non-empty it switches ingestion to allow-list ("whitelist") mode: any file whose
    /// extension is not listed - including files with no extension - is rejected, and
    /// <see cref="BlockedFileExtensions"/> is ignored. When empty (the default) the block-list is used
    /// instead. Matching is case-insensitive. This gives administrators the choice between rejecting a
    /// known set of dangerous types (block-list) or permitting only an explicit set of safe types
    /// (allow-list).
    /// </summary>
    public string[] AllowedFileExtensions { get; set; } = [];

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

    /// <summary>
    /// Effective per-file size cap (bytes) for the single-file URL import: the dedicated
    /// <see cref="MaxFileImportSizeBytes"/> when set, otherwise the shared <see cref="MaxImportSizeBytes"/>.
    /// A value of <c>0</c> means unlimited.
    /// </summary>
    /// <returns>The effective cap in bytes, or <c>0</c> for unlimited.</returns>
    public long EffectiveFileImportCapBytes()
        => MaxFileImportSizeBytes > 0 ? MaxFileImportSizeBytes : MaxImportSizeBytes;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fileName"/> carries an extension present in the
    /// <see cref="BlockedFileExtensions"/> deny-list (case-insensitive). Files without an extension
    /// are not blocked (deny-list semantics).
    /// </summary>
    /// <param name="fileName">The candidate file name (may include a path).</param>
    /// <returns><c>true</c> if the extension is blocked; otherwise <c>false</c>.</returns>
    public bool IsExtensionBlocked(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return false;

        var normalized = ext.TrimStart('.');
        return BlockedFileExtensions is { Length: > 0 }
               && BlockedFileExtensions.Any(b =>
                   string.Equals(b.TrimStart('.'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fileName"/> is accepted by the ingestion extension
    /// policy. This is the single gate used by every ingestion path (single-file URL import, archive
    /// extraction, archive URL import):
    /// <list type="bullet">
    /// <item>When <see cref="AllowedFileExtensions"/> is non-empty the policy is in allow-list mode:
    /// only files whose extension is listed pass; every other file - including files with no
    /// extension - is rejected, and <see cref="BlockedFileExtensions"/> is ignored.</item>
    /// <item>Otherwise the policy is in block-list mode: a file passes unless its extension is present
    /// in <see cref="BlockedFileExtensions"/>. Files without an extension always pass.</item>
    /// </list>
    /// Matching is case-insensitive. A null/blank name is treated as allowed (nothing to judge).
    /// </summary>
    /// <param name="fileName">The candidate file name (may include a path).</param>
    /// <returns><c>true</c> if the file is allowed by the extension policy; otherwise <c>false</c>.</returns>
    public bool IsExtensionAllowed(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;

        var normalized = Path.GetExtension(fileName).TrimStart('.');

        // Allow-list takes precedence: when configured, ONLY listed extensions pass.
        if (AllowedFileExtensions is { Length: > 0 })
            return !string.IsNullOrEmpty(normalized)
                   && AllowedFileExtensions.Any(a =>
                       string.Equals(a.TrimStart('.'), normalized, StringComparison.OrdinalIgnoreCase));

        // Otherwise fall back to the block-list (files without an extension are not blocked).
        return !IsExtensionBlocked(fileName);
    }
}
