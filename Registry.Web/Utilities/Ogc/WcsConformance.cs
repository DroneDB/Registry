namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WCS 2.0 conformance class URIs advertised under
/// <c>ows:ServiceIdentification/ows:Profile</c> in GetCapabilities.
/// Centralised so all WCS-related code references a single source of truth.
/// </summary>
public static class WcsConformance
{
    public const string Core = "http://www.opengis.net/spec/WCS/2.0/conf/core";
    public const string GetKvp = "http://www.opengis.net/spec/WCS_protocol-binding_get-kvp/1.0/conf/get-kvp";

    public static readonly string[] Profiles =
    [
        Core,
        GetKvp
    ];

    /// <summary>
    /// Media types advertised under <c>wcs:ServiceMetadata/wcs:formatSupported</c> and
    /// accepted as the <c>FORMAT</c> KVP value in GetCoverage requests (WCS 2.0 Req31/32).
    /// </summary>
    public static readonly string[] SupportedFormats =
    [
        "image/tiff",
        "image/png",
        "image/jpeg"
    ];
}
