#nullable enable
using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Shared OGC request introspection: detects which OGC service a request targets (from the URL
/// path), negotiates the protocol version per service, and renders the matching exception
/// envelope. Centralising this logic keeps <see cref="OgcExceptionFilter"/> and
/// <see cref="OgcAuthorizationFilter"/> consistent (DRY) so authorization failures and pipeline
/// exceptions always emit a spec-appropriate ExceptionReport for the same request.
/// </summary>
public static class OgcServiceResolver
{
    /// <summary>The OGC service a request targets.</summary>
    public enum Service
    {
        Wms,
        Wmts,
        Wfs,
        Wcs,
        Other
    }

    /// <summary>
    /// Detect the OGC service from the request path using whole path segments. Matching segments
    /// (rather than raw substrings) avoids misclassifying requests when an organization or dataset
    /// slug contains a service token, e.g. <c>/orgs/acme/ds/mywmsdata/wfs</c> must resolve to WFS.
    /// </summary>
    public static Service DetectService(string? path)
    {
        path ??= string.Empty;
        // /wmts must be matched BEFORE /wms to avoid prefix collision.
        if (HasSegment(path, "wmts")) return Service.Wmts;
        if (HasSegment(path, "wms")) return Service.Wms;
        if (HasSegment(path, "wfs")) return Service.Wfs;
        if (HasSegment(path, "wcs")) return Service.Wcs;
        return Service.Other;
    }

    private static bool HasSegment(string path, string segment)
        => path.Contains("/" + segment + "/", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("/" + segment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Negotiate the protocol version for the detected service so the ExceptionReport schema
    /// matches the request. WCS uses the full <see cref="WcsVersionNegotiator"/> rules but never
    /// throws here (falls back to the highest supported version) to avoid masking the original error.
    /// </summary>
    public static string NegotiateVersion(Service service, IQueryCollection query)
    {
        var requested = OgcRequestParser.Get(query, "VERSION")
                        ?? OgcRequestParser.Get(query, "ACCEPTVERSIONS");

        return service switch
        {
            Service.Wms => OgcRequestParser.NegotiateWmsVersion(requested),
            Service.Wfs => string.IsNullOrWhiteSpace(requested) ? "2.0.0" : requested!,
            Service.Wmts => string.IsNullOrWhiteSpace(requested) ? "1.0.0" : requested!,
            Service.Wcs => SafeNegotiateWcsVersion(
                OgcRequestParser.Get(query, "VERSION"),
                OgcRequestParser.Get(query, "ACCEPTVERSIONS")),
            _ => string.IsNullOrWhiteSpace(requested) ? "1.3.0" : requested!
        };
    }

    /// <summary>
    /// Render the exception envelope matching the service and negotiated version:
    /// WMS 1.1.1 → ServiceExceptionReport 1.1.1; WMS 1.3.0 → ServiceExceptionReport (ogc ns);
    /// WCS 1.0 → ServiceExceptionReport 1.2.0; WCS 2.0 → ows/2.0 ExceptionReport;
    /// everything else (WFS 2.0, WMTS 1.0, WCS 1.1) → ows/1.1 ExceptionReport.
    /// </summary>
    public static string FormatException(Service service, string version, string code, string message,
        string? locator = null)
    {
        if (service == Service.Wms && version == "1.1.1")
            return OgcExceptionFormatter.FormatWms111(code, message);
        if (service == Service.Wms)
            return OgcExceptionFormatter.FormatWms130(code, message);
        if (service == Service.Wcs && version.StartsWith("1.0", StringComparison.Ordinal))
            // WCS 1.0.0 predates OWS Common: errors are wrapped in <ServiceExceptionReport>
            // (namespace http://www.opengis.net/ogc, version 1.2.0) - see OGC 03-065r6 §A.4.1.
            return OgcExceptionFormatter.FormatWcs10(code, message);

        // WCS 1.1 → ows/1.1; WCS 2.0 → ows/2.0; WFS 2.0 / WMTS 1.0 → ows/1.1.
        var owsNs = (service == Service.Wcs && version.StartsWith("2.", StringComparison.Ordinal))
            ? "http://www.opengis.net/ows/2.0"
            : "http://www.opengis.net/ows/1.1";
        return OgcExceptionFormatter.FormatOws(code, message, version, locator, owsNs);
    }

    /// <summary>WCS version negotiation that never throws: falls back to the highest supported
    /// version (2.0.1) when the negotiator would otherwise raise, so a secondary negotiation
    /// failure cannot mask the original error being reported.</summary>
    private static string SafeNegotiateWcsVersion(string? rawVersion, string? acceptVersions)
    {
        try
        {
            return WcsVersionNegotiator.Negotiate(rawVersion, acceptVersions);
        }
        catch (Exceptions.OgcException)
        {
            return "2.0.1";
        }
    }
}
