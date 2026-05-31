using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WMTS 1.0.0 conformance constants and parameter validators (OGC 07-057r7).
/// Centralizes the supported formats, styles, and tile-matrix-sets so the controller,
/// manager and capabilities writer cannot drift.
/// </summary>
public static class WmtsConformance
{
    /// <summary>Tile image formats advertised in capabilities and accepted by GetTile.</summary>
    public static readonly string[] SupportedRasterFormats = ["image/png", "image/jpeg"];

    /// <summary>Default vector tile format.</summary>
    public const string VectorFormat = "application/vnd.mapbox-vector-tile";

    /// <summary>Default and only advertised style identifier per layer.</summary>
    public const string DefaultStyle = "default";

    /// <summary>Tile matrix set identifier we advertise (WGS84/Pseudo-Mercator XYZ).</summary>
    public const string DefaultTileMatrixSet = "GoogleMapsCompatible";

    /// <summary>
    /// Validates STYLE against the only style we publish (<see cref="DefaultStyle"/>).
    /// Per OGC 07-057r7 §07.07.1 invalid style → InvalidParameterValue/HTTP 400.
    /// </summary>
    public static void ValidateStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
            throw new OgcException("MissingParameterValue", "Missing required parameter 'style'", 400, "style");
        if (!string.Equals(style, DefaultStyle, StringComparison.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"Style '{style}' is not defined; only '{DefaultStyle}' is supported", 400, "style");
    }

    /// <summary>
    /// Validates FORMAT against the published media types for the given layer kind.
    /// </summary>
    public static void ValidateFormat(string? format, bool isVector)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new OgcException("MissingParameterValue", "Missing required parameter 'format'", 400, "format");
        var allowed = isVector ? [VectorFormat] : SupportedRasterFormats;
        foreach (var f in allowed)
            if (string.Equals(format, f, StringComparison.OrdinalIgnoreCase))
                return;
        throw new OgcException("InvalidParameterValue",
            $"Format '{format}' is not supported (expected one of [{string.Join(", ", allowed)}])",
            400, "format");
    }

    /// <summary>
    /// Validates TILEMATRIXSET against the published sets.
    /// </summary>
    public static void ValidateTileMatrixSet(string? tms)
    {
        if (string.IsNullOrWhiteSpace(tms))
            throw new OgcException("MissingParameterValue",
                "Missing required parameter 'tileMatrixSet'", 400, "tileMatrixSet");
        if (!string.Equals(tms, DefaultTileMatrixSet, StringComparison.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"TileMatrixSet '{tms}' is not defined; only '{DefaultTileMatrixSet}' is supported",
                400, "tileMatrixSet");
    }

    /// <summary>
    /// Validates the SECTIONS KVP value for GetCapabilities and returns the (case-insensitive
    /// canonical) set of requested sections, or <c>null</c> when the parameter is absent
    /// (meaning "all sections"). Unknown section names raise InvalidParameterValue.
    /// </summary>
    public static HashSet<string>? ParseSections(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ServiceIdentification", "ServiceProvider", "OperationsMetadata", "Contents", "Themes", "All" };
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            if (!known.Contains(p))
                throw new OgcException("InvalidParameterValue",
                    $"Unknown section '{p}'", 400, "sections");
            if (string.Equals(p, "All", StringComparison.OrdinalIgnoreCase))
                return null; // null = include all sections
            set.Add(p);
        }
        return set.Count == 0 ? null : set;
    }
}
