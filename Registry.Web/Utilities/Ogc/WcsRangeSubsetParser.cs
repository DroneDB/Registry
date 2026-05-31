using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Registry.Web.Exceptions;
using Registry.Web.Services.Managers.Wcs;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Parses the per-version "give me a subset of bands" KVP shared by every
/// WCS handler:
///  • WCS 2.0 <c>RANGESUBSET</c> (comma-separated band names or 1-based indices, OGC 09-147r3 §8.3)
///  • WCS 1.1 <c>RangeSubset</c> (semi-colon list of <c>field[:interpolation[[band]]]</c>, OGC 07-067r5)
///  • WCS 1.0 ad-hoc <c>BANDS</c> extension (comma-separated indices/names)
///
/// All variants funnel into a single normalised <c>int[]</c> of 1-based band
/// indices that <see cref="IWcsCoverageService.RenderRegion"/> can forward to
/// the native DdbWrapper. Band name lookup is case-insensitive against the
/// names probed via <see cref="WcsRasterInfo.BandNames"/>.
/// </summary>
public static class WcsRangeSubsetParser
{
    /// <summary>WCS 2.0 RANGESUBSET grammar: <c>name(,name)*</c>.</summary>
    public static int[]? ParseRangeSubset20(string? raw, WcsRasterInfo info)
        => ParseCsv(raw, info, "RANGESUBSET");

    /// <summary>
    /// WCS 1.1 RangeSubset grammar (simplified): <c>field(;field)*</c> where each
    /// <c>field</c> may carry an interpolation suffix (e.g. <c>NIR:linear</c>).
    /// We honour only the field component since GDALWarp drives interpolation
    /// uniformly via <c>-r bilinear</c>.
    /// </summary>
    public static int[]? ParseRangeSubset11(string? raw, WcsRasterInfo info)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tokens = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t =>
            {
                var colon = t.IndexOf(':');
                return colon > 0 ? t[..colon].Trim() : t;
            });
        return ResolveTokens(tokens, info, "RangeSubset");
    }

    /// <summary>WCS 1.0 BANDS extension: <c>name|index(,name|index)*</c>.</summary>
    public static int[]? ParseBands10(string? raw, WcsRasterInfo info)
        => ParseCsv(raw, info, "BANDS");

    private static int[]? ParseCsv(string? raw, WcsRasterInfo info, string paramName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ResolveTokens(tokens, info, paramName);
    }

    private static int[] ResolveTokens(IEnumerable<string> tokens, WcsRasterInfo info, string paramName)
    {
        var resolved = new List<int>();
        foreach (var tok in tokens)
        {
            if (string.IsNullOrWhiteSpace(tok)) continue;

            // Try numeric (1-based) first.
            if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                if (info.BandCount > 0 && (idx < 1 || idx > info.BandCount))
                    throw new OgcException("InvalidParameterValue",
                        $"{paramName} band index {idx} is out of range [1,{info.BandCount}]",
                        400, paramName);
                resolved.Add(idx);
                continue;
            }

            // Lookup by name (case-insensitive) against probed band names.
            var match = -1;
            for (var i = 0; i < info.BandNames.Count; i++)
            {
                if (string.Equals(info.BandNames[i], tok, StringComparison.OrdinalIgnoreCase))
                {
                    match = i + 1; // 1-based
                    break;
                }
            }
            if (match < 0)
                throw new OgcException("NoSuchField",
                    $"{paramName} field '{tok}' is not a band of this coverage", 404, paramName);
            resolved.Add(match);
        }

        if (resolved.Count == 0)
            throw new OgcException("InvalidParameterValue",
                $"{paramName} did not resolve to any band", 400, paramName);
        return [.. resolved];
    }
}
