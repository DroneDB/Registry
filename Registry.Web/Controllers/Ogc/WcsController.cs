using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers.Ogc;

/// <summary>WCS 2.0 controller.</summary>
[ApiController]
[ServiceFilter(typeof(BasicAuthFilter))]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WcsController : ControllerBaseEx
{
    private readonly IWcsManager _mgr;
    public WcsController(IWcsManager mgr) { _mgr = mgr; }

    [HttpGet("wcs")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wcs/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
            {
                // WCS 2.0.1 version negotiation (OGC 09-110r4 §8.3.2).
                // If the client explicitly requests only versions the server does not support,
                // return VersionNegotiationFailed so the client knows to retry with 2.0.
                // WCS 1.0.0 / 1.1.x have a completely different XML schema, axis-order rules,
                // GetCoverage parameter encoding and subsetting syntax — implementing them on
                // top of our WCS 2.0 stack is a separate, significant undertaking.
                var acceptVersions = OgcRequestParser.Get(q, "ACCEPTVERSIONS");
                if (!string.IsNullOrWhiteSpace(acceptVersions))
                {
                    var clientVersions = acceptVersions.Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (!clientVersions.Any(v => v.StartsWith("2.", StringComparison.Ordinal)))
                        throw new OgcException("VersionNegotiationFailed",
                            $"WCS 2.0.1 is the only supported version. " +
                            $"In QGIS: set WCS version to '2.0' in the connection dialog. " +
                            $"Client requested: {acceptVersions}", 400, "ACCEPTVERSIONS");
                }
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            }
            case "DESCRIBECOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                return Content(await _mgr.DescribeCoverageAsync(orgSlug, dsSlug, cid),
                    "text/xml; charset=utf-8");
            }
            case "GETCOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");

                // WCS 2.0 Core (OGC 09-110r4) Req29: the only legal value of mediaType is
                // "multipart/related"; any other value MUST raise InvalidParameterValue.
                var mediaType = OgcRequestParser.Get(q, "MEDIATYPE");
                if (!string.IsNullOrWhiteSpace(mediaType) &&
                    !string.Equals(mediaType, "multipart/related", StringComparison.OrdinalIgnoreCase))
                    throw new OgcException("InvalidParameterValue",
                        $"mediaType '{mediaType}' is invalid; only 'multipart/related' is allowed (WCS 2.0 Req29)",
                        400, "mediaType");

                // WCS 2.0 Req32/33: if FORMAT is omitted, return the coverage in its nativeFormat
                // (image/tiff for our GeoTIFF rasters); if FORMAT is present it must match one of
                // wcs:formatSupported advertised by GetCapabilities.
                var format = OgcRequestParser.Get(q, "FORMAT");
                if (string.IsNullOrWhiteSpace(format)) format = "image/tiff";
                else if (!Utilities.Ogc.WcsConformance.SupportedFormats.Contains(format,
                            StringComparer.OrdinalIgnoreCase))
                    throw new OgcException("InvalidParameterValue",
                        $"format '{format}' is not supported", 400, "format");

                double[]? subset = null;
                var subsetParams = q
                    .Where(kv => string.Equals(kv.Key, "SUBSET", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                var bytes = await _mgr.GetCoverageAsync(orgSlug, dsSlug, cid, subsetParams, format);
                return File(bytes, format);
            }
            default:
                throw new OgcException("OperationNotSupported",
                    $"WCS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}
