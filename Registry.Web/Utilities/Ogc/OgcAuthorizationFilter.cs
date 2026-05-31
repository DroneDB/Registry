using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Registry.Web.Services.Managers;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Common authorization for all OGC controllers. Resolves the dataset from {orgSlug}/{dsSlug}
/// route values and enforces <see cref="AccessType.Read"/>. Re-uses the existing JWT cookie auth
/// pipeline transparently and falls back to Basic auth (handled separately by BasicAuthFilter,
/// which is layered before this filter on each controller).
/// </summary>
public class OgcAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IUtils _utils;
    private readonly IAuthManager _authManager;

    public OgcAuthorizationFilter(IUtils utils, IAuthManager authManager)
    {
        _utils = utils;
        _authManager = authManager;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Detect service + negotiate version so denial/error envelopes match the request schema
        // (a 401 on /wfs must not be reported as a WMS 1.3.0 ServiceExceptionReport).
        var service = OgcServiceResolver.DetectService(context.HttpContext.Request.Path.Value);
        var version = OgcServiceResolver.NegotiateVersion(service, context.HttpContext.Request.Query);

        try
        {
            var rv = context.RouteData.Values;
            var orgSlug = rv.TryGetValue("orgSlug", out var os) ? os?.ToString() : null;
            var dsSlug  = rv.TryGetValue("dsSlug",  out var ds) ? ds?.ToString() : null;

            if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
            {
                Fail(context, service, version, 400, "MissingParameterValue", "Missing organization or dataset slug");
                return;
            }

            var dataset = _utils.GetDataset(orgSlug, dsSlug, safe: true);
            if (dataset == null)
            {
                Fail(context, service, version, 404, "NotFound", $"Dataset '{orgSlug}/{dsSlug}' not found");
                return;
            }

            var allowed = await _authManager.RequestAccess(dataset, AccessType.Read);
            if (!allowed)
            {
                BasicAuthFilter.SendBasicAuthRequest(context.HttpContext.Response);
                Fail(context, service, version, 401, "AuthenticationFailed",
                    "Authentication required to access this dataset");
            }
        }
        catch (Exception ex)
        {
            Fail(context, service, version, 500, "NoApplicableCode", ex.Message);
        }
    }

    private static void Fail(AuthorizationFilterContext context, OgcServiceResolver.Service service,
        string version, int status, string code, string message)
    {
        context.Result = new ContentResult
        {
            Content = OgcServiceResolver.FormatException(service, version, code, message),
            ContentType = OgcExceptionFormatter.ContentType,
            StatusCode = status
        };
    }
}
