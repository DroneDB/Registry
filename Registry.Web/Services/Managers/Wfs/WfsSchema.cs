#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Registry.Ports.Json;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using SkiaSharp;
using Registry.Web.Services.Caching;

namespace Registry.Web.Services.Managers.Wfs;

/// <summary>The XML schema of the vector layers (RQ-12, out of <c>WfsManager</c>): DescribeFeatureType.</summary>
public interface IWfsSchema
{
    Task<string> DescribeFeatureTypeAsync(string orgSlug, string dsSlug, string[] typeNames,
        CancellationToken ct = default);
}

/// <summary>Default <see cref="IWfsSchema"/>: one feature type per vector layer, its fields from DroneDB's description.</summary>
public class WfsSchema : OgcManagerBase, IWfsSchema
{
    private readonly ILogger<WfsSchema> _logger;

    public WfsSchema(IDatasetAccessResolver access, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IRecordCache cache, IHttpContextAccessor ctx, ILogger<WfsSchema> logger)
        : base(access, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    public async Task<string> DescribeFeatureTypeAsync(string orgSlug, string dsSlug, string[] typeNames,
        CancellationToken ct = default)
    {
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);

        // Determine target layers. If no typeNames given, describe ALL vector layers.
        IReadOnlyList<OgcLayerDto> targetLayers;
        if (typeNames == null || typeNames.Length == 0)
        {
            var all = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, ct: ct);
            targetLayers = [.. all.Where(l => l.EntryType == EntryType.Vector)];
        }
        else
        {
            var list = new List<OgcLayerDto>();
            foreach (var name in typeNames)
            {
                var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, ct: ct);
                if (layer == null || layer.EntryType != EntryType.Vector)
                    throw new OgcException("InvalidParameterValue",
                        $"Feature type '{name}' is not known to this service", 400, "typeNames");
                list.Add(layer);
            }
            targetLayers = list;
        }

        var sb = new StringBuilder();
        await using var w = CreateXmlWriter(sb);
        await w.WriteStartElementAsync("xsd", "schema", NsXsd);
        await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
        await w.WriteAttributeStringAsync("xmlns", "ddb", null, NsDdb);
        w.WriteAttributeString("targetNamespace", NsDdb);
        w.WriteAttributeString("elementFormDefault", "qualified");
        w.WriteAttributeString("version", "1.0");

        await w.WriteStartElementAsync("xsd", "import", null);
        w.WriteAttributeString("namespace", NsGml);
        w.WriteAttributeString("schemaLocation", "http://schemas.opengis.net/gml/3.2.1/gml.xsd");
        await w.WriteEndElementAsync();

        foreach (var layer in targetLayers)
        {
            var safe = OgcNames.ToNcName(layer.Name);
            IReadOnlyList<LayerField>? fields = null;
            try
            {
                fields = WfsLayerFields.Read(DdbWrapper, ResolveVectorArtifact(ddb, layer), layer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "WFS DescribeFeatureType: failed to describe layer {Layer}; falling back to minimal definition",
                    layer.InnerLayerName);
            }

            // <xsd:element name="safe" type="ddb:safeType" substitutionGroup="gml:AbstractFeature"/>
            await w.WriteStartElementAsync("xsd", "element", null);
            w.WriteAttributeString("name", safe);
            w.WriteAttributeString("type", "ddb:" + safe + "Type");
            w.WriteAttributeString("substitutionGroup", "gml:AbstractFeature");
            await w.WriteEndElementAsync();

            // <xsd:complexType name="safeType"> ...
            await w.WriteStartElementAsync("xsd", "complexType", null);
            w.WriteAttributeString("name", safe + "Type");
            await w.WriteStartElementAsync("xsd", "complexContent", null);
            await w.WriteStartElementAsync("xsd", "extension", null);
            w.WriteAttributeString("base", "gml:AbstractFeatureType");
            await w.WriteStartElementAsync("xsd", "sequence", null);

            await w.WriteStartElementAsync("xsd", "element", null);
            w.WriteAttributeString("name", "geom");
            w.WriteAttributeString("type", "gml:GeometryPropertyType");
            w.WriteAttributeString("minOccurs", "0");
            await w.WriteEndElementAsync();

            // Synthetic ordinal: guarantees a numeric, ordered, non-null comparable property
            // for FES filter conformance tests on every feature type.
            await w.WriteStartElementAsync("xsd", "element", null);
            w.WriteAttributeString("name", "ddbSeq");
            w.WriteAttributeString("type", "xsd:int");
            w.WriteAttributeString("minOccurs", "1");
            await w.WriteEndElementAsync();

            if (fields != null)
            {
                foreach (var f in fields)
                {
                    var fname = f.Name ?? "field";
                    var ftype = (f.Type ?? "String").ToLowerInvariant();
                    var xsdType = ftype switch
                    {
                        "integer" => "xsd:int",
                        "integer64" => "xsd:long",
                        "real" => "xsd:double",
                        "datetime" or "date" => "xsd:dateTime",
                        _ => "xsd:string"
                    };
                    await w.WriteStartElementAsync("xsd", "element", null);
                    w.WriteAttributeString("name", OgcNames.ToNcName(fname));
                    w.WriteAttributeString("type", xsdType);
                    w.WriteAttributeString("minOccurs", "0");
                    await w.WriteEndElementAsync();
                }
            }

            await w.WriteEndElementAsync(); // sequence
            await w.WriteEndElementAsync(); // extension
            await w.WriteEndElementAsync(); // complexContent
            await w.WriteEndElementAsync(); // complexType
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }
}
