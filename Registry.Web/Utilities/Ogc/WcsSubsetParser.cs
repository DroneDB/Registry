using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Parses and validates WCS 2.0 KVP <c>SUBSET</c> parameters per OGC 09-110r4 §9.3.2.2.
/// Enforces:
///  • Req30: axis label must be one of the coverage axis labels (Long, Lat, lat, long, x, y).
///  • Req31: at most one subset per dimension.
///  • Req32/33: trim/slice positions must lie within the coverage extent.
/// Returns a 4-element WGS84 bbox <c>[minLon, minLat, maxLon, maxLat]</c> compatible with
/// <c>WcsManager.GetCoverageAsync</c>, or <c>null</c> when no subset was supplied.
/// </summary>
public static class WcsSubsetParser
{
    private static readonly HashSet<string> LongAliases = new(StringComparer.OrdinalIgnoreCase) { "Long", "long", "lon", "x", "E", "Easting" };
    private static readonly HashSet<string> LatAliases  = new(StringComparer.OrdinalIgnoreCase) { "Lat", "lat", "y", "N", "Northing" };

    public static double[]? Parse(IReadOnlyCollection<string> rawSubsets, double[] coverageBboxWgs84)
    {
        if (rawSubsets == null || rawSubsets.Count == 0) return null;

        var seenAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double minLon = coverageBboxWgs84[0], minLat = coverageBboxWgs84[1];
        double maxLon = coverageBboxWgs84[2], maxLat = coverageBboxWgs84[3];
        var haveLon = false; var haveLat = false;

        foreach (var raw in rawSubsets)
        {
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s)) continue;

            var open  = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open <= 0 || close <= open)
                throw new OgcException("InvalidParameterValue",
                    $"subset '{raw}' is malformed; expected axis(low[,high])", 400, "subset");

            // Multiple subset operations jammed into a single SUBSET value (or any nested
            // parens) - the axis label cannot be reliably identified per OGC 09-110r4 §9.3.2.2.
            if (s.IndexOf('(', open + 1) >= 0 || s.IndexOf(')') != close)
                throw new OgcException("InvalidAxisLabel",
                    $"subset '{raw}' does not identify a valid coverage axis label",
                    404, "subset");

            var axis = s[..open].Trim();
            // Strip optional CRS reference: "axis,crs(...)"
            var commaInAxis = axis.IndexOf(',');
            if (commaInAxis > 0) axis = axis[..commaInAxis].Trim();

            // Req30: axis label must be a known coverage dimension.
            var isLon = LongAliases.Contains(axis);
            var isLat = !isLon && LatAliases.Contains(axis);
            if (!isLon && !isLat)
                throw new OgcException("InvalidAxisLabel",
                    $"subset axis '{axis}' is not one of the coverage axis labels (Long, Lat)",
                    404, "subset");

            // Req31: at most one subset per dimension. Per OGC 09-110r4 §9.3.2.2 and the
            // WCS 2.0 CITE compliance suite, a duplicate axis label in subset parameters
            // is reported as InvalidAxisLabel with HTTP 404 (the second occurrence does
            // not denote a fresh, valid axis selection).
            var canonical = isLon ? "Long" : "Lat";
            if (!seenAxes.Add(canonical))
                throw new OgcException("InvalidAxisLabel",
                    $"axis '{canonical}' appears in more than one subset operation", 404, "subset");

            var positions = s.Substring(open + 1, close - open - 1)
                             .Split(',', StringSplitOptions.TrimEntries);
            if (positions.Length is < 1 or > 2)
                throw new OgcException("InvalidParameterValue",
                    $"subset '{raw}' must contain 1 (slice) or 2 (trim) positions", 400, "subset");

            // Trim quotes from string-typed positions and parse numbers.
            var parsed = new double[positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                var p = positions[i].Trim('"').Trim();
                if (string.Equals(p, "*", StringComparison.Ordinal))
                {
                    // Open-ended position → use coverage extent as bound.
                    parsed[i] = i == 0
                        ? (isLon ? coverageBboxWgs84[0] : coverageBboxWgs84[1])
                        : (isLon ? coverageBboxWgs84[2] : coverageBboxWgs84[3]);
                    continue;
                }
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                    throw new OgcException("InvalidParameterValue",
                        $"subset position '{positions[i]}' is not a valid number", 400, "subset");
            }

            double lo = parsed[0];
            double hi = parsed.Length == 2 ? parsed[1] : parsed[0];
            if (parsed.Length == 2 && lo > hi)
                throw new OgcException("InvalidSubsetting",
                    $"subset '{raw}' has low ({lo}) > high ({hi})", 404, "subset");

            // Req32/33: positions must lie within the coverage extent.
            var axisMin = isLon ? coverageBboxWgs84[0] : coverageBboxWgs84[1];
            var axisMax = isLon ? coverageBboxWgs84[2] : coverageBboxWgs84[3];
            if (hi < axisMin || lo > axisMax)
                throw new OgcException("InvalidSubsetting",
                    $"subset '{raw}' is outside the coverage extent [{axisMin}, {axisMax}]",
                    404, "subset");

            if (isLon) { minLon = Math.Max(minLon, lo); maxLon = Math.Min(maxLon, hi); haveLon = true; }
            else       { minLat = Math.Max(minLat, lo); maxLat = Math.Min(maxLat, hi); haveLat = true; }
        }

        if (!haveLon && !haveLat) return null;
        return [minLon, minLat, maxLon, maxLat];
    }
}
