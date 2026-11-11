using System;
using System.Collections.Generic;
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
    /// The extension of a RESTful tile URL (<c>.../{TileCol}.png</c>) and the media type it stands for: the one map
    /// the RESTful route, the <c>ResourceURL</c> templates of GetCapabilities and the Hub's share templates use
    /// (FR-XC-31).
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> ExtensionFormats =
    [
        new("pbf", VectorFormat),
        new("png", "image/png"),
        new("jpg", "image/jpeg"),
        new("jpeg", "image/jpeg")
    ];

    /// <summary>The route constraint of the RESTful tile URL: the extensions of <see cref="ExtensionFormats"/>.</summary>
    public const string RestfulExtensionPattern = "^(pbf|png|jpe?g)$";

    /// <summary>The media type of a RESTful tile extension (case-insensitive), or <c>null</c> for an unknown one.</summary>
    public static string? FormatForExtension(string? extension)
    {
        foreach (var (ext, format) in ExtensionFormats)
            if (string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase))
                return format;
        return null;
    }

    /// <summary>The extension a RESTful tile URL of <paramref name="format"/> ends with (<c>jpg</c> for JPEG).</summary>
    public static string ExtensionForFormat(string format)
    {
        foreach (var (ext, f) in ExtensionFormats)
            if (string.Equals(f, format, StringComparison.OrdinalIgnoreCase))
                return ext;
        throw new ArgumentException($"No RESTful tile extension for '{format}'", nameof(format));
    }

    /// <summary>
    /// The RESTful tile template of a layer for one format, as GetCapabilities advertises it in a
    /// <c>ResourceURL</c>: <paramref name="restBaseUrl"/> is the service URL, the layer is written in by
    /// its real path (<paramref name="layerName"/>, percent-encoded as one segment: <c>a%2Fb.tif</c>) and the
    /// style, matrix set and tile indexes are placeholders in the order of the route
    /// (<c>{TileMatrix}/{TileRow}/{TileCol}</c>).
    /// </summary>
    public static string RestfulTemplate(string restBaseUrl, string layerName, string format) =>
        $"{restBaseUrl.TrimEnd('/')}/1.0.0/{Uri.EscapeDataString(layerName)}/{{Style}}/{{TileMatrixSet}}/" +
        $"{{TileMatrix}}/{{TileRow}}/{{TileCol}}.{ExtensionForFormat(format)}";

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
    /// Validates TILEMATRIXSET against the published sets: <see cref="DefaultTileMatrixSet"/>, and
    /// <see cref="OgcApiTilesConformance.WebMercatorQuad"/> as an alias of it (the same tiles; the links that name it
    /// were shared before GetCapabilities advertised the template).
    /// </summary>
    public static void ValidateTileMatrixSet(string? tms)
    {
        if (string.IsNullOrWhiteSpace(tms))
            throw new OgcException("MissingParameterValue",
                "Missing required parameter 'tileMatrixSet'", 400, "tileMatrixSet");
        if (!string.Equals(tms, DefaultTileMatrixSet, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tms, OgcApiTilesConformance.WebMercatorQuad, StringComparison.OrdinalIgnoreCase))
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
