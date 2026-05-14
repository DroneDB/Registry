using System.IO;
using System.Text;
using System.Xml;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Renders OGC ExceptionReport XML envelopes. WMS 1.1.1 uses ServiceExceptionReport;
/// WMS 1.3.0 / WFS 2.0.0 / WMTS 1.0 / WCS 2.0 use ows:ExceptionReport.
/// </summary>
public static class OgcExceptionFormatter
{
    public const string ContentType = "text/xml; charset=utf-8";

    /// <summary>StringWriter that reports UTF-8 encoding so XmlWriter declares encoding="utf-8".</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    public static string FormatWms111(string code, string message)
    {
        using var sw = new Utf8StringWriter();
        using var w = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false
        });
        w.WriteStartDocument();
        w.WriteStartElement("ServiceExceptionReport");
        w.WriteAttributeString("version", "1.1.1");
        w.WriteStartElement("ServiceException");
        if (!string.IsNullOrEmpty(code)) w.WriteAttributeString("code", code);
        w.WriteString(message ?? string.Empty);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndDocument();
        w.Flush();
        return sw.ToString();
    }

    public static string FormatOws(string code, string message, string version = "2.0.0", string? locator = null)
    {
        using var sw = new Utf8StringWriter();
        using var w = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false
        });
        w.WriteStartDocument();
        w.WriteStartElement("ows", "ExceptionReport", "http://www.opengis.net/ows/1.1");
        w.WriteAttributeString("version", version);
        w.WriteStartElement("ows", "Exception", "http://www.opengis.net/ows/1.1");
        if (!string.IsNullOrEmpty(code)) w.WriteAttributeString("exceptionCode", code);
        if (!string.IsNullOrEmpty(locator)) w.WriteAttributeString("locator", locator);
        w.WriteStartElement("ows", "ExceptionText", "http://www.opengis.net/ows/1.1");
        w.WriteString(message ?? string.Empty);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndDocument();
        w.Flush();
        return sw.ToString();
    }

    /// <summary>
    /// Pick the right envelope flavour given the OGC service version of the failing request.
    /// </summary>
    public static string Format(OgcException ex, string serviceVersion)
    {
        return serviceVersion switch
        {
            "1.1.1" => FormatWms111(ex.Code, ex.Message),
            _ => FormatOws(ex.Code, ex.Message, serviceVersion, ex.Locator)
        };
    }
}
