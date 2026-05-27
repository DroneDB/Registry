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
        var query = context.HttpContext.Request.Query;

        // Determine service from path. /wmts must be matched BEFORE /wms to avoid prefix collision.
        var isWmts = path.Contains("/wmts", System.StringComparison.OrdinalIgnoreCase);
        var isWms  = !isWmts && path.Contains("/wms", System.StringComparison.OrdinalIgnoreCase);
        var isWfs  = path.Contains("/wfs", System.StringComparison.OrdinalIgnoreCase);
        var isWcs  = path.Contains("/wcs", System.StringComparison.OrdinalIgnoreCase);

        // Negotiate version per service so the ExceptionReport schema matches.
        var requested = OgcRequestParser.Get(query, "VERSION")
                        ?? OgcRequestParser.Get(query, "ACCEPTVERSIONS");
        string version;
        if (isWms)
            version = OgcRequestParser.NegotiateWmsVersion(requested);
        else if (isWfs)
            version = string.IsNullOrWhiteSpace(requested) ? "2.0.0" : requested!;
        else if (isWmts)
            version = string.IsNullOrWhiteSpace(requested) ? "1.0.0" : requested!;
        else if (isWcs)
            version = string.IsNullOrWhiteSpace(requested) ? "2.0.1" : requested!;
        else
            version = string.IsNullOrWhiteSpace(requested) ? "1.3.0" : requested!;

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

        string xml;
        if (isWms && version == "1.1.1")
            xml = OgcExceptionFormatter.FormatWms111(code, message);
        else if (isWms)
            xml = OgcExceptionFormatter.FormatWms130(code, message);
        else
        {
            // WCS 2.0 imports ows/2.0; WFS 2.0 / WMTS 1.0 import ows/1.1.
            var owsNs = isWcs ? "http://www.opengis.net/ows/2.0" : "http://www.opengis.net/ows/1.1";
            xml = OgcExceptionFormatter.FormatOws(code, message, version, locator, owsNs);
        }

        context.Result = new ContentResult
        {
            Content = xml,
            ContentType = OgcExceptionFormatter.ContentType,
            StatusCode = status
        };
        context.ExceptionHandled = true;
    }
}
