#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Registry.Web.Services.Import;

/// <summary>
/// Pure helpers for the single-file URL import (<c>import-file</c> tool): http/https URL validation
/// and safe file-name derivation. Deliberately free of I/O so it can be unit-tested in isolation and
/// shared between the web-layer verify path (<see cref="Registry.Web.Services.Managers.FileUrlImportManager"/>)
/// and the worker-side execute path (<see cref="Registry.Web.Services.HeavyTasks.Tools.FileUrlImportTool"/>).
/// </summary>
public static class FileImportPolicy
{
    /// <summary>Maximum length (characters) of a single file-name/path segment (NTFS limit, safe across platforms).</summary>
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// Deterministic, cross-platform set of characters that are invalid in a single name segment
    /// (the Windows superset). Using an explicit set instead of <see cref="Path.GetInvalidFileNameChars"/>
    /// keeps sanitization identical on Windows and Linux, so a file extracted on one platform cannot
    /// carry a name that breaks or is ambiguous on another. Control characters are handled separately.
    /// </summary>
    private static readonly HashSet<char> InvalidNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Windows reserved device names (case-insensitive), including the superscript COM/LPT variants.
    /// A segment equal to one of these (optionally with an extension) is prefixed with an underscore.
    /// Ref: learn.microsoft.com/windows/win32/fileio/naming-a-file
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
    };

    /// <summary>
    /// Validates that <paramref name="url"/> is an absolute http/https URL and returns the parsed <see cref="Uri"/>.
    /// </summary>
    /// <param name="url">The candidate URL.</param>
    /// <returns>The parsed absolute <see cref="Uri"/>.</returns>
    /// <exception cref="ArgumentException">The URL is missing or is not a valid http/https URL.</exception>
    public static Uri ParseHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL is required.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"The URL is not a valid http/https URL: {url}");

        return uri;
    }

    /// <summary>
    /// Derives a safe bare file name for the imported file, preferring the server-supplied
    /// Content-Disposition file name and falling back to the last URL path segment.
    /// </summary>
    /// <param name="uri">The (already validated) source URL.</param>
    /// <param name="contentDispositionFileName">The file name advertised via Content-Disposition, if any.</param>
    /// <returns>A sanitized bare file name (never empty).</returns>
    public static string DeriveFileName(Uri uri, string? contentDispositionFileName = null)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var candidate = !string.IsNullOrWhiteSpace(contentDispositionFileName)
            ? contentDispositionFileName
            : Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));

        return SanitizeFileName(candidate);
    }

    /// <summary>
    /// Reduces <paramref name="name"/> to a bare, safe file name: strips directory components and any
    /// query/fragment, removes traversal, replaces characters invalid in a file name, rejects Windows
    /// reserved device names (by prefixing an underscore) and caps the length. Falls back to
    /// <c>imported-file</c> when nothing usable remains.
    /// </summary>
    /// <param name="name">The candidate name (may include path or query parts).</param>
    /// <returns>A safe bare file name.</returns>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "imported-file";

        // Strip any directory components (both separators).
        var bare = name.Replace('\\', '/');
        var slash = bare.LastIndexOf('/');
        if (slash >= 0) bare = bare[(slash + 1)..];

        // Drop any query/fragment that leaked in from a raw URL segment.
        var q = bare.IndexOfAny(['?', '#']);
        if (q >= 0) bare = bare[..q];

        bare = bare.Trim().Trim('.');
        bare = ReplaceInvalidChars(bare);

        // Reject only if the name is entirely dots/spaces/underscores (nothing usable); otherwise
        // preserve legitimate leading/trailing underscores (e.g. "_thumbnail.jpg", "report_").
        if (string.IsNullOrWhiteSpace(bare.Trim('.', ' ', '_')))
            return "imported-file";

        return ApplyReservedAndLength(bare);
    }

    /// <summary>
    /// Sanitizes every segment of a forward-slash relative path independently: each segment has its
    /// invalid characters replaced, Windows reserved device names prefixed, trailing dots/spaces
    /// stripped and length capped, while legitimate leading dots (hidden files such as
    /// <c>.gitignore</c>) are preserved. Segments that collapse to nothing are dropped. The caller is
    /// responsible for the anti-traversal checks (rooted paths, <c>..</c>) BEFORE calling this method;
    /// this method only normalizes the individual names, it does not judge traversal.
    /// </summary>
    /// <param name="relativePath">The forward-slash (or backslash) relative path to sanitize.</param>
    /// <returns>The sanitized relative path (forward-slash separated), possibly empty.</returns>
    public static string SanitizePathSegments(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;

        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var outParts = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            // Keep leading dots (hidden files) but drop trailing dots/spaces (Windows ambiguity).
            var seg = ReplaceInvalidChars(part).Trim(' ').TrimEnd('.', ' ');
            if (seg.Length == 0) continue; // a fully-invalid segment is dropped

            outParts.Add(ApplyReservedAndLength(seg));
        }

        return string.Join('/', outParts);
    }

    /// <summary>Replaces control characters and characters invalid in a name with an underscore.</summary>
    private static string ReplaceInvalidChars(string s)
    {
        if (s.Length == 0) return s;

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c < ' ' || c == (char)127 || InvalidNameChars.Contains(c) ? '_' : c);

        return sb.ToString();
    }

    /// <summary>
    /// Prefixes an underscore when the name matches a Windows reserved device name and caps the total
    /// length to <see cref="MaxFileNameLength"/>, preserving the extension when truncating.
    /// </summary>
    private static string ApplyReservedAndLength(string s)
    {
        var stem = Path.GetFileNameWithoutExtension(s);
        if (ReservedNames.Contains(s) || ReservedNames.Contains(stem))
            s = "_" + s;

        if (s.Length > MaxFileNameLength)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length >= MaxFileNameLength) ext = string.Empty;
            s = s[..(MaxFileNameLength - ext.Length)] + ext;
        }

        return s;
    }
}
