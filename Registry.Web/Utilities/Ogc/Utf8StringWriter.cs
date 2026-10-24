using System.IO;
using System.Text;

namespace Registry.Web.Utilities.Ogc;

/// <summary>StringWriter that reports UTF-8 encoding so XmlWriter declares encoding="utf-8".</summary>
internal sealed class Utf8StringWriter : StringWriter
{
    public override Encoding Encoding => Encoding.UTF8;
}
