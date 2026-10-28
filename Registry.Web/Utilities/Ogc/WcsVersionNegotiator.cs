using System;
using System.Linq;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WCS version negotiation per OGC 09-110r4 §8.3.2 (negotiation rules of OWS Common 2.0).
/// Honored KVP: <c>VERSION</c> (1.0 / 1.1 style) and <c>ACCEPTVERSIONS</c> (OWS 2.0 style).
/// Returns the first mutually supported version in the client's ACCEPTVERSIONS preference order
/// (or the highest supported version when no version is requested), or throws
/// <see cref="OgcException"/> with code <c>VersionNegotiationFailed</c>.
/// </summary>
public static class WcsVersionNegotiator
{
    /// <summary>Negotiate against <see cref="WcsConformance.SupportedVersions"/>.</summary>
    /// <param name="rawVersion">Value of the <c>VERSION</c> KVP (may be null).</param>
    /// <param name="acceptVersions">Value of the <c>ACCEPTVERSIONS</c> KVP, comma-separated (may be null).</param>
    /// <returns>One of "1.0.0", "1.1.1", "2.0.1".</returns>
    public static string Negotiate(string? rawVersion, string? acceptVersions)
    {
        // 1) ACCEPTVERSIONS wins when present (OWS 2.0 style): pick the first supported version
        //    in the client's preference order.
        if (!string.IsNullOrWhiteSpace(acceptVersions))
        {
            var requested = acceptVersions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var v in requested)
            {
                var match = MatchVersion(v);
                if (match != null) return match;
            }

            throw new OgcException("VersionNegotiationFailed",
                $"No supported WCS version. Server supports: {string.Join(", ", WcsConformance.SupportedVersions)}; " +
                $"client requested: {acceptVersions}", 400, "AcceptVersions");
        }

        // 2) Single VERSION (WCS 1.0/1.1 style): pick exact or closest lower.
        if (string.IsNullOrWhiteSpace(rawVersion))
            return WcsConformance.SupportedVersions[0];

        {
            var direct = MatchVersion(rawVersion);
            if (direct != null) return direct;
            // OGC 03-065r6 §6.2.1: server selects highest supported version <= requested.
            var requested = ParseVersion(rawVersion);

            if (requested == null)
                return WcsConformance.SupportedVersions[0];

            var lowerOrEqual = WcsConformance.SupportedVersions
                .Select(v => (v, parsed: ParseVersion(v)))
                .Where(t => t.parsed != null && Compare(t.parsed!.Value, requested.Value) <= 0)
                .OrderByDescending(t => t.parsed, VersionComparer.Instance)
                .Select(t => t.v)
                .FirstOrDefault();
            if (lowerOrEqual != null) return lowerOrEqual;
        }

        // 3) No version requested: return the highest supported (WCS 2.0.1).
        return WcsConformance.SupportedVersions[0];
    }

    /// <summary>Exact match (e.g. "1.0.0", "1.1.1", "1.1.0" → "1.1.1", "2.0.0" → "2.0.1").
    /// Matches by major.minor when patch differs.</summary>
    private static string? MatchVersion(string v)
    {
        v = v.Trim();
        foreach (var sup in WcsConformance.SupportedVersions)
        {
            if (string.Equals(v, sup, StringComparison.Ordinal)) return sup;
        }

        // Tolerate "1.0" / "1.1" / "2.0" by mapping to the supported patch version.
        var p = ParseVersion(v);
        if (p == null) return null;
        return (from sup in WcsConformance.SupportedVersions
            let sp = ParseVersion(sup)
            where sp != null && sp.Value.Major == p.Value.Major && sp.Value.Minor == p.Value.Minor
            select sup).FirstOrDefault();
    }

    private static (int Major, int Minor, int Patch)? ParseVersion(string v)
    {
        var parts = v.Split('.');
        if (parts.Length is < 2 or > 3) return null;
        if (!int.TryParse(parts[0], out var maj)) return null;
        if (!int.TryParse(parts[1], out var min)) return null;
        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch)) return null;
        return (maj, min, patch);
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        return a.Major != b.Major ? a.Major.CompareTo(b.Major) :
            a.Minor != b.Minor ? a.Minor.CompareTo(b.Minor) : a.Patch.CompareTo(b.Patch);
    }

    private sealed class VersionComparer : System.Collections.Generic.IComparer<(int Major, int Minor, int Patch)?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare((int Major, int Minor, int Patch)? x, (int Major, int Minor, int Patch)? y)
        {
            return x switch
            {
                null when !y.HasValue => 0,
                null => -1,
                _ => !y.HasValue ? 1 : WcsVersionNegotiator.Compare(x.Value, y.Value)
            };
        }
    }
}