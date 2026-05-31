using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers.Ogc;

/// <summary>WCS controller. Supports versions 1.0.0 / 1.1.1 / 2.0.1 via version
/// negotiation; the per-version wire format is handled by
/// <see cref="Services.Managers.Wcs.IWcsProtocolHandler"/> implementations.</summary>
[ApiController]
[ServiceFilter(typeof(BasicAuthFilter))]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WcsController : ControllerBaseEx
{
    private readonly IWcsManager _mgr;

    public WcsController(IWcsManager mgr)
    {
        _mgr = mgr;
    }

    [HttpGet("wcs")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wcs/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;

        // OWS Common 2.0 §11.5.1: SERVICE is mandatory in KVP encoding. The WCS 2.0 ATS
        // (req18 / OWSCommon_exception_with_exception_text) sends malformed requests and
        // expects the server to return MissingParameterValue / InvalidParameterValue with
        // proper HTTP status codes instead of falling through to a 200 capabilities document.
        var service = OgcRequestParser.Get(q, "SERVICE");
        if (string.IsNullOrWhiteSpace(service))
            throw new OgcException("MissingParameterValue",
                "SERVICE parameter is required", 400, "service");
        if (!string.Equals(service, "WCS", StringComparison.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"SERVICE '{service}' not supported by WCS endpoint", 400, "service");

        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        var rawVersion = OgcRequestParser.Get(q, "VERSION");
        var acceptVersions = OgcRequestParser.Get(q, "ACCEPTVERSIONS");

        // For GetCapabilities a missing VERSION is legitimate (the client is discovering it);
        // we still negotiate so the response body matches the chosen profile.
        var version = WcsVersionNegotiator.Negotiate(rawVersion, acceptVersions);

        // WCS 1.0 §6.1.2: GetCoverage and DescribeCoverage REQUIRE VERSION. For 1.1 / 2.0 the
        // version may also come from ACCEPTVERSIONS; we treat both as equivalent here.
        var op = request.ToUpperInvariant();
        if ((op == "GETCOVERAGE" || op == "DESCRIBECOVERAGE")
            && string.IsNullOrWhiteSpace(rawVersion) && string.IsNullOrWhiteSpace(acceptVersions))
            throw new OgcException("MissingParameterValue",
                "VERSION is required for DescribeCoverage and GetCoverage", 400, "VERSION");

        switch (op)
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, version, folderPath),
                    "text/xml; charset=utf-8");

            case "DESCRIBECOVERAGE":
            {
                // Per-version parameter name: WCS 1.0 → COVERAGE, WCS 1.1 → IDENTIFIER(S),
                // WCS 2.0 → CoverageId. Accepting cross-version aliases breaks the WCS 2.0
                // ATS req18 negative case which sends a misspelled "Coverage" parameter and
                // expects MissingParameterValue. Be strict per version.
                string? cid;
                string locator;
                if (version.StartsWith("1.0", StringComparison.Ordinal))
                {
                    cid = OgcRequestParser.Get(q, "COVERAGE");
                    locator = "COVERAGE";
                }
                else if (version.StartsWith("1.1", StringComparison.Ordinal))
                {
                    cid = OgcRequestParser.GetAny(q, "IDENTIFIER", "IDENTIFIERS");
                    locator = "Identifier";
                }
                else
                {
                    cid = OgcRequestParser.Get(q, "COVERAGEID");
                    locator = "CoverageId";
                }
                if (string.IsNullOrWhiteSpace(cid))
                    throw new OgcException("MissingParameterValue",
                        $"{locator} is required", 400, locator);
                return Content(await _mgr.DescribeCoverageAsync(orgSlug, dsSlug, version, cid),
                    "text/xml; charset=utf-8");
            }

            case "GETCOVERAGE":
            {
                var result = await _mgr.GetCoverageAsync(orgSlug, dsSlug, version, q);
                return File(result.Bytes, result.ContentType);
            }

            default:
                throw new OgcException("OperationNotSupported",
                    $"WCS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}