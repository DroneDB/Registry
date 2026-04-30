using System;

namespace Registry.Web.Services.Hub;

/// <summary>
/// Compares Hub semantic versions to decide whether the on-disk Hub
/// needs to be replaced by the embedded one.
/// </summary>
/// <remarks>
/// The comparison is intentionally lenient: any non-parseable on-disk version
/// (missing file, blank string, malformed text) is treated as "older" and
/// triggers a re-extraction. This guarantees the on-disk Hub is upgraded
/// when transitioning from a Registry version that did not stamp a version,
/// or when the marker file has been tampered with.
/// </remarks>
public static class HubVersionComparer
{
    /// <summary>
    /// Returns <c>true</c> when the embedded Hub should overwrite the on-disk one.
    /// </summary>
    /// <param name="onDiskVersion">Content of the on-disk version marker, or <c>null</c> if missing.</param>
    /// <param name="embeddedVersion">Version stamped into the embedded archive.</param>
    public static bool ShouldUpgrade(string onDiskVersion, string embeddedVersion)
    {
        if (string.IsNullOrWhiteSpace(embeddedVersion))
            return false;

        if (!TryParse(embeddedVersion, out var embedded))
            return false;

        if (string.IsNullOrWhiteSpace(onDiskVersion))
            return true;

        if (!TryParse(onDiskVersion, out var onDisk))
            return true;

        return embedded > onDisk;
    }

    /// <summary>
    /// Parses a semver-like string (major.minor.patch[-prerelease][+build]).
    /// Pre-release and build suffixes are ignored — only the numeric core is compared.
    /// </summary>
    public static bool TryParse(string raw, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();

        // Strip pre-release / build metadata
        var dashIdx = trimmed.IndexOfAny(['-', '+']);
        if (dashIdx >= 0) trimmed = trimmed[..dashIdx];

        // Pad missing components: "1" → "1.0.0", "1.2" → "1.2.0"
        var dots = 0;
        foreach (var c in trimmed)
            if (c == '.') dots++;
        for (var i = dots; i < 2; i++) trimmed += ".0";

        return Version.TryParse(trimmed, out version);
    }
}
