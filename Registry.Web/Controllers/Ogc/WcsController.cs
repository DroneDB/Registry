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
                // WCS 1.0 uses COVERAGE, 1.1 / 2.0 use CoverageId (alias-tolerant).
                var cid = OgcRequestParser.GetAny(q, "COVERAGE", "COVERAGEID", "IDENTIFIER", "IDENTIFIERS")
                          ?? string.Empty;
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