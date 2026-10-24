using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Translates exceptions thrown by OGC controllers into the appropriate XML response
/// (ExceptionReport / ServiceExceptionReport) following the OGC version negotiated by the request.
/// OGC and DDB exceptions keep their OGC-specific codes and messages; every other exception takes its
/// status, message and log level from <see cref="ApiExceptionClassifier"/>, like the JSON API.
/// Always marks the exception handled so the global
/// <see cref="ApiExceptionFilter"/> never double-writes the response with its JSON envelope.
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
            case DdbBusyException:
                // Transient DB write contention: 503 + Retry-After, matching the API pipeline's
                // unified transient mapping (ApiExceptionClassifier) rather than the 500 arm below.
                code = "ServiceUnavailable";
                message = "Dataset index is busy; retry shortly.";
                status = 503;
                context.HttpContext.Response.Headers.RetryAfter = "2";
                break;
            case DdbBuildInProgressException:
                // Kernel build lock held: 503 + Retry-After (same rationale as above).
                code = "ServiceUnavailable";
                message = "Dataset build is currently in progress; retry shortly.";
                status = 503;
                context.HttpContext.Response.Headers.RetryAfter = "2";
                break;
            // The generic native failure; the typed ones (retryable, shutdown, invalid arguments, not
            // found) take the API's mapping in the default arm
            case DdbException dex when dex.GetType() == typeof(DdbException):
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
                // Every other exception: the API's status, message and log level (one mapping, RQ-05),
                // in the OGC envelope
                var d = ApiExceptionClassifier.Classify(context.Exception, context.HttpContext);
                ApiExceptionClassifier.Log(_logger, d, context.Exception, context.HttpContext.Request);
                if (ApiExceptionClassifier.IsClientGone(d))
                {
                    // Nobody reads an exception report any more (RQ-13)
                    context.Result = new StatusCodeResult(d.StatusCode);
                    context.ExceptionHandled = true;
                    return;
                }

                status = d.StatusCode;
                message = d.Message;
                code = OgcCodeFor(status);
                if (d.RetryAfterSeconds is { } retryAfter)
                    context.HttpContext.Response.Headers.RetryAfter =
                        retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>The OGC exception code of an HTTP status (the codes this filter has always used).</summary>
    internal static string OgcCodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "InvalidParameterValue",
        StatusCodes.Status401Unauthorized => "AuthenticationFailed",
        StatusCodes.Status403Forbidden => "AccessDenied",
        StatusCodes.Status404NotFound => "NotFound",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        _ => "NoApplicableCode"
    };
}