#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Rewrites the GML 3.2 that OGR writes for a vector query (an <c>ogr:FeatureCollection</c> of
/// <c>ogr:featureMember</c>s) into the WFS 2.0 shape the OGC ETS expects, in one streaming pass
/// (XmlReader to XmlWriter) instead of DOM copies of the whole payload:
/// <list type="bullet">
/// <item><c>wfs:FeatureCollection</c> with <c>numberReturned</c>/<c>numberMatched</c>, <c>wfs:boundedBy</c>, <c>wfs:member</c>s;</item>
/// <item>each feature becomes <c>ddb:&lt;feature type&gt;</c> with gml:id "&lt;feature type&gt;.&lt;fid&gt;" (<see cref="WfsFeatureIds"/>);</item>
/// <item>properties move to the ddb namespace (NCName), gml properties are kept, empty leaf properties
/// are dropped (they break ETS value parsing), property attributes are dropped;</item>
/// <item>a <c>ddb:ddbSeq</c> integer (0-based position in the response) is appended to every feature
/// for the ETS ordering predicates.</item>
/// </list>
/// </summary>
public static class WfsFeatureCollectionWriter
{
    private const string NsXmlns = "http://www.w3.org/2000/xmlns/";

    private const string SchemaLocation =
        "http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd " +
        "http://www.opengis.net/gml/3.2 http://schemas.opengis.net/gml/3.2.1/gml.xsd";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Prohibit
    };

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false)
    };

    /// <summary>
    /// The <c>wfs:FeatureCollection</c> for <paramref name="ogrGml"/>.
    /// </summary>
    /// <param name="ogrGml">OGR GML output (DDBQueryVector, format gml).</param>
    /// <param name="layerName">Layer (feature type) name; its NCName names the features.</param>
    /// <param name="numberMatched">Features matching the request before paging, or null to report
    /// the number returned.</param>
    public static string ToFeatureCollection(string ogrGml, string layerName, long? numberMatched = null)
    {
        ArgumentNullException.ThrowIfNull(ogrGml);
        var featureType = OgcNames.ToNcName(layerName);
        var returned = CountFeatures(ogrGml);
        var names = new PropertyNames();

        // The output is about as long as the input
        using var output = new PooledUtf8TextWriter(ogrGml.Length + 1024);
        using (var r = XmlReader.Create(new StringReader(ogrGml), ReaderSettings))
        using (var w = XmlWriter.Create(output, WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("wfs", "FeatureCollection", OgcNamespaces.Wfs);
            w.WriteAttributeString("xmlns", "gml", null, OgcNamespaces.Gml);
            w.WriteAttributeString("xmlns", "ddb", null, OgcNamespaces.Ddb);
            w.WriteAttributeString("xmlns", "xsi", null, OgcNamespaces.Xsi);
            w.WriteAttributeString("xsi", "schemaLocation", OgcNamespaces.Xsi, SchemaLocation);
            w.WriteAttributeString("timeStamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteAttributeString("numberReturned", returned.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("numberMatched", (numberMatched ?? returned).ToString(CultureInfo.InvariantCulture));

            var seq = 0;
            var boundedBy = false;
            ForEachChildElement(r, () =>
            {
                if (!boundedBy && r.LocalName == "boundedBy" && r.NamespaceURI == OgcNamespaces.Gml)
                {
                    boundedBy = true;
                    w.WriteStartElement("wfs", "boundedBy", OgcNamespaces.Wfs);
                    CopyContent(r, w);
                    w.WriteEndElement();
                }
                else if (r.LocalName == "featureMember")
                {
                    w.WriteStartElement("wfs", "member", OgcNamespaces.Wfs);
                    ForEachChildElement(r, () => WriteFeature(r, w, featureType, names, seq++));
                    w.WriteEndElement();
                }
                else
                    r.Skip();
            });

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return output.ToString();
    }

    /// <summary>
    /// The first feature of <paramref name="ogrGml"/> as a document of its own (the WFS 2.0
    /// GetFeatureById response, OGC 09-025 §7.9.3.6), or null when there is none.
    /// </summary>
    public static string? ToSingleFeature(string ogrGml, string layerName)
    {
        ArgumentNullException.ThrowIfNull(ogrGml);
        var featureType = OgcNames.ToNcName(layerName);

        using var output = new PooledUtf8TextWriter();
        var found = false;
        using (var r = XmlReader.Create(new StringReader(ogrGml), ReaderSettings))
        using (var w = XmlWriter.Create(output, WriterSettings))
        {
            w.WriteStartDocument();
            ForEachChildElement(r, () =>
            {
                if (found || r.LocalName != "featureMember")
                {
                    r.Skip();
                    return;
                }

                ForEachChildElement(r, () =>
                {
                    if (found)
                    {
                        r.Skip();
                        return;
                    }

                    found = true;
                    WriteFeature(r, w, featureType, new PropertyNames(), 0, declareNamespaces: true);
                });
            });
            if (found)
                w.WriteEndDocument();
        }

        return found ? output.ToString() : null;
    }

    /// <summary>Number of features (elements inside featureMembers) in the OGR GML.</summary>
    private static int CountFeatures(string ogrGml)
    {
        using var r = XmlReader.Create(new StringReader(ogrGml), ReaderSettings);
        var count = 0;
        ForEachChildElement(r, () =>
        {
            if (r.LocalName == "featureMember")
                ForEachChildElement(r, () =>
                {
                    count++;
                    r.Skip();
                });
            else
                r.Skip();
        });
        return count;
    }

    /// <summary>
    /// Calls <paramref name="onElement"/> with the reader on each child element of the current
    /// element (or of the document root, before the first read); the callback must consume the
    /// element. Leaves the reader after the parent's end tag.
    /// </summary>
    private static void ForEachChildElement(XmlReader r, Action onElement)
    {
        if (r.ReadState == ReadState.Initial)
        {
            r.MoveToContent();
            if (r.NodeType != XmlNodeType.Element)
                return;
        }

        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        var depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element)
                onElement();
            else
                r.Read();
        }

        r.Read();
    }

    /// <summary>Copies the content of the current element (not the element itself).</summary>
    private static void CopyContent(XmlReader r, XmlWriter w)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        var depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            CopyNode(r, w);
        r.Read();
    }

    /// <summary>
    /// Copies the current node (an element with its subtree, or text) and moves past it, like
    /// <see cref="XmlWriter.WriteNode(XmlReader, bool)"/> but streaming text in chunks instead of
    /// materializing each value (coordinate lists are most of the payload).
    /// </summary>
    private static void CopyNode(XmlReader r, XmlWriter w)
    {
        var buffer = ArrayPool<char>.Shared.Rent(8192);
        try
        {
            var depth = r.Depth;
            do
            {
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        w.WriteStartElement(r.Prefix, r.LocalName, r.NamespaceURI);
                        w.WriteAttributes(r, true);
                        if (r.IsEmptyElement)
                            w.WriteEndElement();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                        int read;
                        while ((read = r.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                            w.WriteChars(buffer, 0, read);
                        break;
                    case XmlNodeType.CDATA:
                        w.WriteCData(r.Value);
                        break;
                    case XmlNodeType.EndElement:
                        w.WriteFullEndElement();
                        break;
                }
            } while (r.Read() && (r.Depth > depth || (r.Depth == depth && r.NodeType == XmlNodeType.EndElement)));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void WriteFeature(XmlReader r, XmlWriter w, string featureType, PropertyNames names, int seq,
        bool declareNamespaces = false)
    {
        w.WriteStartElement("ddb", featureType, OgcNamespaces.Ddb);
        if (declareNamespaces)
            w.WriteAttributeString("xmlns", "gml", null, OgcNamespaces.Gml);

        if (r.MoveToFirstAttribute())
        {
            do
            {
                if (r.NamespaceURI == NsXmlns)
                    continue;
                var value = r.LocalName == "id" && r.NamespaceURI == OgcNamespaces.Gml
                    ? WfsFeatureIds.FromOgr(r.Value, featureType)
                    : r.Value;
                w.WriteAttributeString(r.Prefix, r.LocalName, r.NamespaceURI, value);
            } while (r.MoveToNextAttribute());

            r.MoveToElement();
        }

        ForEachChildElement(r, () =>
        {
            if (r.NamespaceURI == OgcNamespaces.Gml)
                CopyNode(r, w);
            else
                WriteProperty(r, w, names);
        });

        w.WriteElementString("ddb", "ddbSeq", OgcNamespaces.Ddb, seq.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    /// <summary>
    /// Writes an OGR property as ddb:&lt;NCName&gt; with its content; drops it when it is a leaf with
    /// blank text (empty values break ETS consumers that parse them as numbers or dates).
    /// </summary>
    private static void WriteProperty(XmlReader r, XmlWriter w, PropertyNames names)
    {
        var name = names.Get(r.LocalName);
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        var depth = r.Depth;
        r.Read();

        // Text before the first child element decides whether this is an empty leaf (usually
        // one text node: the value)
        (XmlNodeType Type, string Value) first = default;
        List<(XmlNodeType Type, string Value)>? more = null;
        var blank = true;
        while (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
        {
            var text = (r.NodeType, r.Value);
            blank &= string.IsNullOrWhiteSpace(text.Value);
            if (first.Value == null)
                first = text;
            else
                (more ??= []).Add(text);
            r.Read();
        }

        if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth && blank)
        {
            r.Read();
            return;
        }

        w.WriteStartElement("ddb", name, OgcNamespaces.Ddb);
        if (first.Value != null)
            WriteText(w, first);
        if (more != null)
            foreach (var text in more)
                WriteText(w, text);

        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            CopyNode(r, w);
        r.Read();
        w.WriteEndElement();
    }

    private static void WriteText(XmlWriter w, (XmlNodeType Type, string Value) text)
    {
        if (text.Type == XmlNodeType.CDATA)
            w.WriteCData(text.Value);
        else
            w.WriteString(text.Value);
    }

    /// <summary>Property element names as NCNames, converted once per name.</summary>
    private sealed class PropertyNames
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

        public string Get(string ogrName)
        {
            if (!_names.TryGetValue(ogrName, out var name))
                _names[ogrName] = name = OgcNames.ToNcName(ogrName);
            return name;
        }
    }
}
