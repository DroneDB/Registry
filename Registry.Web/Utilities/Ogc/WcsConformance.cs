using Registry.Web.Services.Managers.Wcs;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WCS conformance metadata advertised in <c>GetCapabilities</c>. Centralised so every
/// version-specific handler (WCS 1.0 / 1.1 / 2.0) references a single source of truth
/// and the negotiator can enumerate supported versions in one place.
/// </summary>
public static class WcsConformance
{
    // WCS 2.0 conformance class URIs (advertised under ows:Profile).
    public const string Core = "http://www.opengis.net/spec/WCS/2.0/conf/core";
    public const string GetKvp = "http://www.opengis.net/spec/WCS_protocol-binding_get-kvp/1.0/conf/get-kvp";

    /// <summary>WCS 2.0 profiles (used by <see cref="WcsProtocol20Handler"/>).</summary>
    public static readonly string[] Profiles = [Core, GetKvp];

    /// <summary>Supported wire versions (ordered: highest first).</summary>
    public static readonly string[] SupportedVersions = ["2.0.1", "1.1.1", "1.0.0"];

    /// <summary>WCS 2.0 / 1.1 format MIME types (<c>FORMAT</c> KVP / <c>wcs:formatSupported</c>).</summary>
    public static readonly string[] SupportedFormats =
    [
        "image/tiff",
        "image/png",
        "image/jpeg"
    ];

    /// <summary>WCS 1.0 advertises formats by short name in <c>supportedFormats/formats</c>.</summary>
    public static readonly string[] SupportedFormatsWcs10 =
    [
        "GeoTIFF",
        "PNG",
        "JPEG"
    ];

    /// <summary>Translate a WCS 1.0 short format name (or pass-through MIME) into a MIME type.</summary>
    public static string NormalizeWcs10Format(string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return "image/tiff";
        return format.Trim().ToLowerInvariant() switch
        {
            "geotiff" or "tiff" or "image/tiff" or "image/geotiff" => "image/tiff",
            "png" or "image/png" => "image/png",
            "jpeg" or "jpg" or "image/jpeg" => "image/jpeg",
            _ => format
        };
    }

    /// <summary>Supported CRSs advertised in WCS 1.0/1.1 capabilities.</summary>
    public static readonly string[] SupportedCrs =
    [
        "EPSG:4326",
        "EPSG:3857"
    ];

    /// <summary>
    /// Normalise an OGC CRS reference (URI, URN or short authority code) to the
    /// canonical "EPSG:nnnn" form expected by GDAL / DroneDB. Recognised inputs:
    ///   • http://www.opengis.net/def/crs/EPSG/0/nnnn
    ///   • urn:ogc:def:crs:EPSG::nnnn
    ///   • EPSG:nnnn
    ///   • http://www.opengis.net/def/crs/OGC/1.3/CRS84 (mapped to EPSG:4326)
    /// Returns an empty string for empty/null input. Unrecognised values are
    /// returned verbatim so the native side can attempt a best-effort import.
    /// </summary>
    public static string NormalizeCrs(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs)) return string.Empty;
        var s = crs.Trim();
        if (s.Equals("http://www.opengis.net/def/crs/OGC/1.3/CRS84",
                System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("urn:ogc:def:crs:OGC::CRS84", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("CRS84", System.StringComparison.OrdinalIgnoreCase))
            return "EPSG:4326";

        // http://www.opengis.net/def/crs/EPSG/0/4326 → EPSG:4326
        const string opengisPrefix = "http://www.opengis.net/def/crs/EPSG/";
        if (s.StartsWith(opengisPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            var rest = s[opengisPrefix.Length..]; // "0/4326"
            var slash = rest.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < rest.Length)
                return "EPSG:" + rest[(slash + 1)..];
        }

        // urn:ogc:def:crs:EPSG::4326 → EPSG:4326
        const string urnPrefix = "urn:ogc:def:crs:EPSG";
        if (s.StartsWith(urnPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            var idx = s.LastIndexOf(':');
            if (idx >= 0 && idx + 1 < s.Length)
                return "EPSG:" + s[(idx + 1)..];
        }
        return s;
    }
}
