using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Registry.Web.Services.OrganizationAssets;

/// <summary>
/// Makes an SVG that is safe to serve from the origin of the Hub: the document is copied through an allow-list, so what
/// is not known to be harmless is not in the copy. Shapes, paths, text and gradients stay; scripts, styles sheets, event
/// attributes, embedded HTML (<c>foreignObject</c>), images, links and every reference to something outside the document
/// (<c>url(...)</c> to anything but an id of the document, a <c>href</c> to anything but one) are dropped. A
/// <c>DOCTYPE</c> is ignored and never fetched, and no entity is expanded, so there is no external entity to load.
/// </summary>
/// <remarks>
/// It reads and writes as a stream, element by element, so a deeply nested document cannot exhaust the stack; the
/// depth is bounded as well. The style sheets of the document are not kept, but what their class rules paint is: an
/// element gets the declarations of its classes in its <c>style</c> attribute (<see cref="SvgClassStyles"/>), through the
/// same allow-list as a <c>style</c> of its own, so a logo exported with classes (as design tools do) keeps its colours.
/// </remarks>
public static partial class SvgSanitizer
{
    /// <summary>The namespace of SVG; an element in another one is not kept.</summary>
    public const string SvgNamespace = "http://www.w3.org/2000/svg";

    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

    /// <summary>The deepest nesting accepted.</summary>
    public const int MaxDepth = 64;

    private static readonly FrozenSet<string> Elements = FrozenSet.ToFrozenSet(
    [
        "svg", "g", "defs", "symbol", "use", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "title", "desc", "linearGradient", "radialGradient", "stop", "clipPath", "mask"
    ], StringComparer.Ordinal);

    // The elements whose text is kept: a name, a description, a label. Elsewhere the text of the document is dropped
    private static readonly FrozenSet<string> TextElements = FrozenSet.ToFrozenSet(
        ["text", "tspan", "title", "desc"], StringComparer.Ordinal);

    // The elements that may point at another element of the document (a gradient that inherits another one, a use)
    private static readonly FrozenSet<string> ReferencingElements = FrozenSet.ToFrozenSet(
        ["use", "linearGradient", "radialGradient"], StringComparer.Ordinal);

    // The properties of the style attribute kept, and also attributes: how a shape is painted and a text is set
    private static readonly FrozenSet<string> PresentationProperties = FrozenSet.ToFrozenSet(
    [
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "stroke-opacity", "opacity", "stop-color",
        "stop-opacity", "clip-rule", "display", "visibility", "font-family", "font-size", "font-weight", "font-style",
        "text-anchor", "letter-spacing"
    ], StringComparer.Ordinal);

    private static readonly FrozenSet<string> Attributes = FrozenSet.ToFrozenSet(
    [
        .. PresentationProperties,
        "id", "viewBox", "preserveAspectRatio", "width", "height", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r",
        "rx", "ry", "fx", "fy", "d", "points", "transform", "offset", "gradientUnits", "gradientTransform",
        "spreadMethod", "clip-path", "mask", "clipPathUnits", "maskUnits", "maskContentUnits", "dx", "dy",
        "dominant-baseline", "version"
    ], StringComparer.Ordinal);

    // A reference to a thing of the document: #id (what a gradient, a clip path or a use needs)
    [GeneratedRegex(@"^#[A-Za-z0-9_.:-]+\z")]
    private static partial Regex LocalReference();

    // url( followed by anything but an id of the document: an address, a data URI, a scheme
    [GeneratedRegex(@"url\(\s*(?!['""]?\s*#)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();

    // Script and legacy hooks in a value, and a CSS escape (which can spell any of the above)
    [GeneratedRegex(@"javascript:|expression\(|@import|\\", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveValue();

    /// <summary>
    /// The safe copy of <paramref name="svg"/> (UTF-8, no byte order mark).
    /// </summary>
    /// <exception cref="NotSvgDocumentException">Not an SVG document (not XML, or its root is not <c>svg</c>).</exception>
    /// <exception cref="InvalidDataException">An SVG nested deeper than <see cref="MaxDepth"/>.</exception>
    public static byte[] Sanitize(byte[] svg)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true
        };

        using var output = new MemoryStream();
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false),
            CloseOutput = false
        };

        try
        {
            var classStyles = SvgClassStyles.Parse(StyleSheets(svg, readerSettings));
            using (var reader = XmlReader.Create(new MemoryStream(svg), readerSettings))
            using (var writer = XmlWriter.Create(output, writerSettings))
            {
                Copy(reader, writer, classStyles);
            }
        }
        catch (XmlException ex)
        {
            throw new NotSvgDocumentException("The file is not a valid SVG document", ex);
        }

        return output.ToArray();
    }

    // The text of the style elements of the document, in their order: a first pass, as a sheet may come after the
    // elements it paints
    private static string StyleSheets(byte[] svg, XmlReaderSettings settings)
    {
        var sheets = new StringBuilder();
        var styleDepth = -1;
        using var reader = XmlReader.Create(new MemoryStream(svg), settings);
        while (reader.Read())
        {
            EnsureDepth(reader);
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when styleDepth < 0 && !reader.IsEmptyElement &&
                                              reader.LocalName == "style" && reader.NamespaceURI == SvgNamespace:
                    styleDepth = reader.Depth;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA when styleDepth >= 0:
                    sheets.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when reader.Depth == styleDepth:
                    styleDepth = -1;
                    sheets.Append('\n');
                    break;
            }
        }

        return sheets.ToString();
    }

    private static void Copy(XmlReader reader, XmlWriter writer, SvgClassStyles classStyles)
    {
        var open = new Stack<string>();
        var rootWritten = false;

        reader.Read();
        while (!reader.EOF)
        {
            EnsureDepth(reader);

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (!IsKept(reader, rootWritten))
                    {
                        // The whole subtree: what is in a script or an image is not to be looked into
                        reader.Skip();
                        continue;
                    }

                    rootWritten = true;
                    WriteElement(reader, writer, classStyles);
                    if (reader.IsEmptyElement)
                        writer.WriteEndElement();
                    else
                        open.Push(reader.LocalName);
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (open.Count > 0 && TextElements.Contains(open.Peek()))
                        writer.WriteString(reader.Value);
                    break;

                case XmlNodeType.EndElement:
                    open.Pop();
                    writer.WriteEndElement();
                    break;
            }

            reader.Read();
        }

        if (!rootWritten)
            throw new NotSvgDocumentException("The file is not an SVG document");
    }

    private static void EnsureDepth(XmlReader reader)
    {
        if (reader.Depth > MaxDepth)
            throw new InvalidDataException("The SVG is nested too deeply");
    }

    // The root is svg in the SVG namespace; every other element is one of the allow-list, in that namespace
    private static bool IsKept(XmlReader reader, bool rootWritten) =>
        reader.NamespaceURI == SvgNamespace && Elements.Contains(reader.LocalName) &&
        (rootWritten || reader.LocalName == "svg");

    private static void WriteElement(XmlReader reader, XmlWriter writer, SvgClassStyles classStyles)
    {
        var element = reader.LocalName;
        writer.WriteStartElement(element, SvgNamespace);

        string? classes = null, style = null;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                // The class and the style make one style attribute, written once the others are
                if (reader.NamespaceURI == "" && reader.LocalName is "class" or "style")
                {
                    if (reader.LocalName == "class") classes = reader.Value;
                    else style = reader.Value;
                    continue;
                }

                var value = AttributeValue(element, reader);
                if (value != null)
                    writer.WriteAttributeString(reader.LocalName, value);
            } while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        var safeStyle = SafeStyle(classStyles.DeclarationsOf(classes).Concat(SvgClassStyles.Declarations(style)));
        if (safeStyle != null)
            writer.WriteAttributeString("style", safeStyle);
    }

    // The value to write for the attribute the reader is on, or null to drop it
    private static string? AttributeValue(string element, XmlReader attribute)
    {
        var name = attribute.LocalName;
        var value = attribute.Value;

        if (name == "href" && attribute.NamespaceURI is "" or XlinkNamespace)
            return ReferencingElements.Contains(element) && LocalReference().IsMatch(value) ? value : null;

        if (attribute.NamespaceURI != "")
            return null;

        return Attributes.Contains(name) && IsSafe(value) ? value : null;
    }

    private static bool IsSafe(string value) => !ExternalUrl().IsMatch(value) && !ActiveValue().IsMatch(value);

    // The declarations that are painting and safe, as a style attribute (a later one of a property wins, as in CSS);
    // null when none is left
    private static string? SafeStyle(IEnumerable<KeyValuePair<string, string>> declarations)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (property, value) in declarations)
        {
            if (!PresentationProperties.Contains(property) || !IsSafe(value))
                continue;
            if (kept.TryAdd(property, value))
                order.Add(property);
            else
                kept[property] = value;
        }

        return order.Count == 0 ? null : string.Join(';', order.Select(property => $"{property}:{kept[property]}"));
    }
}

/// <summary>
/// The bytes are not an SVG document at all (not XML, or a root that is not <c>svg</c>), as opposed to an SVG that is
/// refused for what it holds: an upload of another kind of file is told the types it may be, not why it is no SVG.
/// </summary>
public sealed class NotSvgDocumentException : Exception
{
    public NotSvgDocumentException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
