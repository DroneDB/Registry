using System;
using System.Diagnostics.CodeAnalysis;

namespace Registry.Web.Services.Hub;

/// <summary>
/// Compares Hub stamps (<c>version</c> or <c>version+build</c>, the content of <c>version.txt</c>) to decide
/// whether the on-disk Hub needs to be replaced by the embedded one.
/// </summary>
/// <remarks>
/// The build part is a hash of the Hub build: the same version of another build (a Hub that changed without a
/// version bump) replaces the on-disk Hub too. A stamp without a build (written by an earlier Registry) is older
/// than the same version with one.
/// 
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
    /// <param name="embeddedVersion">Stamp of the embedded archive.</param>
    public static bool ShouldUpgrade(string? onDiskVersion, string? embeddedVersion)
    {
        if (string.IsNullOrWhiteSpace(embeddedVersion))
            return false;

        if (!TryParse(embeddedVersion, out var embedded))
            return false;

        if (string.IsNullOrWhiteSpace(onDiskVersion))
            return true;

        if (!TryParse(onDiskVersion, out var onDisk))
            return true;

        if (embedded != onDisk)
            return embedded > onDisk;

        // The same version: the same Hub only when the embedded build is the one on disk. An embedded stamp
        // without a build says nothing about the build, so the version decides
        return BuildOf(embeddedVersion) is { } embeddedBuild &&
               !string.Equals(embeddedBuild, BuildOf(onDiskVersion), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Hub version of a stamp: the text before the build (<c>+</c>), which is the version the client compares with its own.</summary>
    public static string? VersionOf(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp))
            return null;

        var trimmed = stamp.Trim();
        var plus = trimmed.IndexOf('+');
        return plus < 0 ? trimmed : trimmed[..plus];
    }

    /// <summary>The build of a stamp (the text after the first <c>+</c>), or null when it has none.</summary>
    public static string? BuildOf(string? stamp)
    {
        var trimmed = stamp?.Trim();
        var plus = trimmed?.IndexOf('+') ?? -1;
        return plus < 0 || plus == trimmed!.Length - 1 ? null : trimmed[(plus + 1)..];
    }

    /// <summary>
    /// Parses a semver-like string (major.minor.patch[-prerelease][+build]).
    /// Pre-release and build suffixes are ignored - only the numeric core is compared.
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out Version? version)
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
