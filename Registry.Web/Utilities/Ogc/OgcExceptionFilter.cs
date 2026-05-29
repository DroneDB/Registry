using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Translates exceptions thrown by OGC controllers into the appropriate XML response
/// (ExceptionReport / ServiceExceptionReport) following the OGC version negotiated by the request.
/// </summary>
public class OgcExceptionFilter : IExceptionFilter
{
    private readonly ILogger<OgcExceptionFilter> _logger;

    public OgcExceptionFilter(ILogger<OgcExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;

        // Detect service + negotiate version via the shared resolver so this filter and
        // OgcAuthorizationFilter always agree on the envelope flavor for the same request.
        var service = OgcServiceResolver.DetectService(path);
        var version = OgcServiceResolver.NegotiateVersion(service, context.HttpContext.Request.Query);

        string code;
        string message;
        int status;
        string? locator = null;

        switch (context.Exception)
        {
            case OgcException oex:
                code = oex.Code;
                message = oex.Message;
                status = oex.HttpStatusCode;
                locator = oex.Locator;
                break;
            case BadRequestException bex:
                code = "InvalidParameterValue";
                message = bex.Message;
                status = 400;
                break;
            case NotFoundException nex:
                code = "NotFound";
                message = nex.Message;
                status = 404;
                break;
            case UnauthorizedException uex:
                code = "AuthenticationFailed";
                message = uex.Message;
                status = 401;
                break;
            case ConflictException cex:
                code = "Conflict";
                message = cex.Message;
                status = 409;
                break;
            case DdbException dex:
                // DroneDB native errors surface here. Map missing-dependency / build-in-progress
                // patterns from the message; otherwise return 500 with the original message
                // (avoids leaking implementation details while preserving diagnostic info).
                var msg = dex.Message ?? string.Empty;
                if (msg.Contains("BUILDDEPMISSING", System.StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("dependency missing", System.StringComparison.OrdinalIgnoreCase))
                {
                    code = "ServiceUnavailable";
                    message = "Required build artifact is missing; rebuild the dataset and retry.";
                    status = 503;
                }
                else if (msg.Contains("BUILDINPROGRESS", System.StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("build in progress", System.StringComparison.OrdinalIgnoreCase))
                {
                    code = "ServiceUnavailable";
                    message = "Dataset build is currently in progress; retry shortly.";
                    status = 503;
                }
                else
                {
                    _logger.LogWarning(dex, "DroneDB error in OGC pipeline: {Path}", path);
                    code = "NoApplicableCode";
                    message = msg.Length == 0 ? "DroneDB error" : msg;
                    status = 500;
                }

                break;
            default:
                _logger.LogError(context.Exception, "Unhandled exception in OGC pipeline: {Path}", path);
                code = "NoApplicableCode";
                message = "Internal server error";
                status = 500;
                break;
        }

        var xml = OgcServiceResolver.FormatException(service, version, code, message, locator);

        context.Result = new ContentResult
        {
            Content = xml,
            ContentType = OgcExceptionFormatter.ContentType,
            StatusCode = status
        };
        context.ExceptionHandled = true;
    }
}