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

namespace Registry.Web.Controllers;

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
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "DESCRIBECOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                return Content(await _mgr.DescribeCoverageAsync(orgSlug, dsSlug, cid),
                    "text/xml; charset=utf-8");
            }
            case "GETCOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                double[]? subset = null;
                var subsetParams = q
                    .Where(kv => string.Equals(kv.Key, "SUBSET", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                if (subsetParams.Length >= 2)
                {
                    // Best-effort minimal SUBSET parser: lat(minLat,maxLat),long(minLon,maxLon)
                    double? minLat = null, maxLat = null, minLon = null, maxLon = null;
                    foreach (var part in subsetParams)
                    {
                        var open = part.IndexOf('(');
                        var close = part.IndexOf(')');
                        if (open < 0 || close < 0) continue;
                        var axis = part[..open].Trim().ToLowerInvariant();
                        var range = part.Substring(open + 1, close - open - 1).Split(',');
                        if (range.Length != 2) continue;
                        if (!double.TryParse(range[0], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var lo) ||
                            !double.TryParse(range[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var hi)) continue;
                        if (axis.StartsWith("lat") || axis == "y") { minLat = lo; maxLat = hi; }
                        else if (axis.StartsWith("lon") || axis.StartsWith("long") || axis == "x")
                            { minLon = lo; maxLon = hi; }
                    }
                    if (minLon.HasValue && minLat.HasValue && maxLon.HasValue && maxLat.HasValue)
                        subset = new[] { minLon.Value, minLat.Value, maxLon.Value, maxLat.Value };
                }
                var bytes = await _mgr.GetCoverageAsync(orgSlug, dsSlug, cid, subset, format);
                return File(bytes, format);
            }
            default:
                throw new OgcException("OperationNotSupported",
                    $"WCS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}
