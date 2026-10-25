#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Common authorization for the OGC controllers and the MVT endpoint: resolves the dataset of the
/// {orgSlug}/{dsSlug} route values through <see cref="IDatasetAccessResolver"/> with
/// <see cref="AccessType.Read"/>, whose scoped grant the managers of the request then reuse. Re-uses the
/// JWT cookie auth pipeline and accepts Basic auth (handled by BasicAuthFilter: each controller declares
/// <see cref="Filters.BasicAuthenticationAttribute"/>, which runs before this filter, so the access check
/// sees the Basic user). A refusal or a client error is written in the OGC exception envelope of the
/// request; the status and message of every exception other than a refused access come from
/// <see cref="ApiExceptionClassifier"/>, like the JSON API.
/// </summary>
public class OgcAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IDatasetAccessResolver _access;
    private readonly ILogger<OgcAuthorizationFilter> _logger;

    public OgcAuthorizationFilter(IDatasetAccessResolver access, ILogger<OgcAuthorizationFilter> logger)
    {
        _access = access;
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Detect service + negotiate version so denial/error envelopes match the request schema
        // (a 401 on /wfs must not be reported as a WMS 1.3.0 ServiceExceptionReport).
        var service = OgcServiceResolver.DetectService(context.HttpContext.Request.Path.Value);
        var version = OgcServiceResolver.NegotiateVersion(service, context.HttpContext.Request.Query);

        var rv = context.RouteData.Values;
        var orgSlug = rv.TryGetValue("orgSlug", out var os) ? os?.ToString() : null;
        var dsSlug = rv.TryGetValue("dsSlug", out var ds) ? ds?.ToString() : null;

        if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
        {
            Fail(context, service, version, 400, "MissingParameterValue", "Missing organization or dataset slug");
            return;
        }

        try
        {
            await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: context.HttpContext.RequestAborted);
        }
        catch (ForbiddenException)
        {
            // A signed-in caller who lacks access gets 403; an anonymous one 401 with the Basic
            // challenge, so the OGC client asks for credentials
            var status = ApiExceptionClassifier.AccessDeniedStatus(
                context.HttpContext.User.Identity?.IsAuthenticated == true);
            if (status == StatusCodes.Status401Unauthorized)
                BasicAuthFilter.SendBasicAuthRequest(context.HttpContext.Response);
            Fail(context, service, version, status, OgcExceptionFilter.OgcCodeFor(status),
                status == StatusCodes.Status401Unauthorized
                    ? "Authentication required to access this dataset"
                    : "The current user cannot access this dataset");
        }
        catch (NotFoundException)
        {
            Fail(context, service, version, StatusCodes.Status404NotFound, "NotFound",
                $"Dataset '{orgSlug}/{dsSlug}' not found");
        }
        catch (Exception ex)
        {
            // Any other error (an invalid slug, a busy index): the API's status and message, logged once
            var d = ApiExceptionClassifier.Classify(ex, context.HttpContext);
            _logger.Log(d.Level, ex, "OGC authorization failed: {Path}", context.HttpContext.Request.Path);
            if (ApiExceptionClassifier.IsClientGone(d))
            {
                // Nobody reads an exception report any more
                context.Result = new StatusCodeResult(d.StatusCode);
                return;
            }

            if (d.RetryAfterSeconds is { } retryAfter)
                context.HttpContext.Response.Headers.RetryAfter =
                    retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Fail(context, service, version, d.StatusCode, OgcExceptionFilter.OgcCodeFor(d.StatusCode), d.Message);
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
