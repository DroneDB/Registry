using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
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
        // Best-effort version recovery from the originating query string.
        // Choose a service-appropriate default when VERSION/ACCEPTVERSIONS is missing,
        // so OWS ExceptionReport version matches the schematron of the failing service.
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        string defaultVersion = "1.3.0";
        if (path.Contains("/wfs", System.StringComparison.OrdinalIgnoreCase))
            defaultVersion = "2.0.0";
        else if (path.Contains("/wmts", System.StringComparison.OrdinalIgnoreCase))
            defaultVersion = "1.0.0";
        else if (path.Contains("/wcs", System.StringComparison.OrdinalIgnoreCase))
            defaultVersion = "2.0.1";
        var version = OgcRequestParser.Get(context.HttpContext.Request.Query, "VERSION")
                      ?? OgcRequestParser.Get(context.HttpContext.Request.Query, "ACCEPTVERSIONS")
                      ?? defaultVersion;

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
            default:
                _logger.LogError(context.Exception, "Unhandled exception in OGC pipeline");
                code = "NoApplicableCode";
                message = "Internal server error";
                status = 500;
                break;
        }

        // Use WMS-specific ServiceExceptionReport for /wms endpoints.
        var isWms = path.Contains("/wms", System.StringComparison.OrdinalIgnoreCase)
                    && !path.Contains("/wmts", System.StringComparison.OrdinalIgnoreCase);
        string xml;
        if (isWms && version == "1.1.1")
            xml = OgcExceptionFormatter.FormatWms111(code, message);
        else if (isWms)
            xml = OgcExceptionFormatter.FormatWms130(code, message);
        else
            xml = OgcExceptionFormatter.FormatOws(code, message, version, locator);

        context.Result = new ContentResult
        {
            Content = xml,
            ContentType = OgcExceptionFormatter.ContentType,
            StatusCode = status
        };
        context.ExceptionHandled = true;
    }
}
