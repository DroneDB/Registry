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
}
