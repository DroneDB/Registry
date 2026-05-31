using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// DRY parser for OGC KVP query strings. Case-insensitive lookup, BBOX parsing with
/// WMS 1.3.0 EPSG:4326 axis-order awareness, version negotiation and parameter aliasing
/// (WFS 1.x typeName vs 2.0 typeNames, count vs maxFeatures, ...).
/// </summary>
public static class OgcRequestParser
{
    /// <summary>
    /// Case-insensitive query-string lookup. Returns null when missing.
    /// When a key appears multiple times (or with different casings), the first
    /// non-empty value is returned (CITE test suites duplicate KVP names on purpose
    /// when probing case-insensitivity; OGC semantics treat the parameter as a
    /// single value, not a comma-joined list).
    /// </summary>
    public static string? Get(IQueryCollection q, string key)
    {
        string? firstMatch = null;
        foreach (var kv in q)
        {
            if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var v in kv.Value)
            {
                if (!string.IsNullOrEmpty(v))
                    return v;
            }
            firstMatch ??= kv.Value.ToString();
        }
        return firstMatch;
    }

    /// <summary>First value among <paramref name="keys"/> (alias resolution).</summary>
    public static string? GetAny(IQueryCollection q, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = Get(q, key);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public static string GetRequired(IQueryCollection q, string key)
    {
        var v = Get(q, key);
        if (string.IsNullOrWhiteSpace(v))
            throw new OgcException("MissingParameterValue", $"Missing required parameter '{key}'", 400, key);
        return v;
    }

    public static int GetInt(IQueryCollection q, string key, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        var v = Get(q, key);
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new OgcException("InvalidParameterValue", $"Parameter '{key}' is not an integer", 400, key);
        return Math.Clamp(parsed, min, max);
    }

    public static double GetDouble(IQueryCollection q, string key)
    {
        var v = GetRequired(q, key);
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            throw new OgcException("InvalidParameterValue", $"Parameter '{key}' is not a number", 400, key);
        return parsed;
    }

    public static string[]? GetList(IQueryCollection q, string key, char sep = ',')
    {
        var v = Get(q, key);
        if (string.IsNullOrWhiteSpace(v)) return null;
        return v.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Parse a "minx,miny,maxx,maxy[,crs]" BBOX string.
    /// </summary>
    /// <param name="bboxStr">Raw BBOX value.</param>
    /// <param name="crsOverride">Optional CRS forced from CRS/SRS parameter.</param>
    /// <param name="wmsVersion">WMS version: "1.3.0" honors axis-order swap for EPSG:4326-family.</param>
    /// <returns>[minLon, minLat, maxLon, maxLat] always in lon/lat order, plus resolved CRS.</returns>
    public static (double[] Bbox, string Crs) ParseBbox(string bboxStr, string? crsOverride = null, string? wmsVersion = null)
    {
        if (string.IsNullOrWhiteSpace(bboxStr))
            throw new OgcException("MissingParameterValue", "Missing BBOX", 400, "BBOX");

        var parts = bboxStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 && parts.Length != 5)
            throw new OgcException("InvalidParameterValue", "BBOX must have 4 or 5 comma-separated values", 400, "BBOX");

        var raw = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out raw[i]))
                throw new OgcException("InvalidParameterValue", $"BBOX value '{parts[i]}' is not a number", 400, "BBOX");
        }

        var crs = crsOverride;
        if (parts.Length == 5 && string.IsNullOrWhiteSpace(crs)) crs = parts[4];
        if (string.IsNullOrWhiteSpace(crs)) crs = "EPSG:4326";

        // WMS 1.3.0 axis-order quirk: EPSG:4326 is lat,lon (north,east).
        // Internally we always work lon,lat (minX,minY,maxX,maxY).
        var swap = IsLatLonOrder(crs, wmsVersion);
        var bbox = swap
            ? [raw[1], raw[0], raw[3], raw[2]]
            : raw;

        // WMS 1.3.0 Annex B.6: bbox is invalid when minx>=maxx or miny>=maxy.
        if (bbox[0] >= bbox[2] || bbox[1] >= bbox[3])
            throw new OgcException("InvalidParameterValue", "BBOX min must be < max", 400, "BBOX");

        return (bbox, crs);
    }

    /// <summary>
    /// True if the OGC version + CRS pair requires lat,lon axis order
    /// (WMS 1.3.0 + WFS 2.0.0 for geographic CRSs).
    /// </summary>
    public static bool IsLatLonOrder(string crs, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        if (!IsGeographicCrs(crs)) return false;
        // WMS 1.3.0 / WFS 2.0.0 use lat,lon. WMS 1.1.1 always lon,lat.
        return version.StartsWith("1.3", StringComparison.Ordinal)
               || version.StartsWith("2.", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> GeographicCrsCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EPSG:4326", "urn:ogc:def:crs:EPSG::4326",
        "CRS:84", // lon,lat by definition
        "EPSG:4269", "urn:ogc:def:crs:EPSG::4269"
    };

    public static bool IsGeographicCrs(string crs)
    {
        if (string.IsNullOrWhiteSpace(crs)) return false;
        if (string.Equals(crs, "CRS:84", StringComparison.OrdinalIgnoreCase)) return false;
        return GeographicCrsCodes.Contains(crs);
    }

    /// <summary>Negotiate the closest supported version. WMS supports 1.3.0, 1.1.1.</summary>
    public static string NegotiateWmsVersion(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "1.3.0";
        return requested switch
        {
            "1.1.1" => "1.1.1",
            _ => "1.3.0"
        };
    }
}
