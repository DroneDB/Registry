using System;

namespace Registry.Web.Exceptions;

/// <summary>
/// OGC-protocol-level exception. The OgcExceptionFilter renders these as the appropriate
/// XML envelope (ServiceExceptionReport for WMS 1.1.1, ows:ExceptionReport for 1.3.0/WFS/WMTS/WCS)
/// or JSON for OGC API endpoints.
/// </summary>
public class OgcException : Exception
{
    /// <summary>OGC standard exception code (e.g. "InvalidParameterValue", "LayerNotDefined").</summary>
    public string Code { get; }

    /// <summary>HTTP status code to return (default 400).</summary>
    public int HttpStatusCode { get; }

    /// <summary>Optional locator (parameter name that triggered the error).</summary>
    public string? Locator { get; }

    public OgcException(string code, string message, int httpStatusCode = 400, string? locator = null)
        : base(message)
    {
        Code = code;
        HttpStatusCode = httpStatusCode;
        Locator = locator;
    }
}
