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

/// <summary>The features of the vector layers (RQ-12, out of <c>WfsManager</c>): GetFeature, GetFeatureById and GetPropertyValue.</summary>
public interface IWfsFeatureQuery
{
    Task<string> GetFeatureAsync(string orgSlug, string dsSlug, string typeName,
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null, CancellationToken ct = default);
    Task<string> GetFeatureByIdAsync(string orgSlug, string dsSlug, string id, string outputFormat,
        CancellationToken ct = default);
    Task<string> GetPropertyValueAsync(string orgSlug, string dsSlug, string typeName,
        string valueReference, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IWfsFeatureQuery"/>: BBOX, resource id and FES filters with pagination, evaluated by DroneDB
/// when its SQL can express them and by <see cref="WfsLocalFilter"/> otherwise (no CQL).
/// </summary>
public class WfsFeatureQuery : OgcManagerBase, IWfsFeatureQuery
{
    private readonly ILogger<WfsFeatureQuery> _logger;

    public WfsFeatureQuery(OgcManagerDependencies dependencies, ILogger<WfsFeatureQuery> logger)
        : base(dependencies)
    {
        _logger = logger;
    }

    public async Task<string> GetFeatureAsync(string orgSlug, string dsSlug, string typeName,
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null, CancellationToken ct = default)
    {
        if (count <= 0) count = 1000;
        count = Math.Clamp(count, 1, 10000);
        if (startIndex < 0) startIndex = 0;

        // CRS validation: only EPSG:4326 / urn:ogc:def:crs:EPSG::4326 / CRS84 supported.
        if (!string.IsNullOrWhiteSpace(bboxCrs) && !IsSupportedCrs(bboxCrs!))
            throw new OgcException("InvalidParameterValue", $"CRS '{bboxCrs}' is not supported", 400, "srsName");

        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, typeName, ct: ct)
                    ?? throw new OgcException("InvalidParameterValue", $"Unknown typeNames '{typeName}'", 404, "typeNames");
        if (layer.EntryType != EntryType.Vector)
            throw new OgcException("OperationNotSupported", "WFS supports only Vector layers", 400);

        // resourceId: must be non-empty when provided.
        long[]? fids = null;
        if (!string.IsNullOrEmpty(resourceId))
        {
            var ids = resourceId!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (ids.Length == 0)
                throw new OgcException("InvalidParameterValue", "RESOURCEID is empty", 400, "RESOURCEID");
            fids = FeatureIdsOf(ids, layer);
            if (fids.Length == 0)
                return WfsFeatureCollectionWriter.ToFeatureCollection(EmptyOgrCollection, layer.Name);
        }

        var gpkg = ResolveVectorArtifact(ddb, layer);
        var internalFormat = MapWfsOutputFormat(outputFormat);
        // RESOURCEID selects by feature id in DroneDB (GeoPackage primary key), so paging applies
        // to the selected features. A FES Filter selects before paging too (OGC 09-025r2 §7.9.2.4):
        // DroneDB evaluates the filters FesSqlTranslator can express; the others are evaluated
        // here on up to 10 000 features.
        var query = new VectorQuery(layer.InnerLayerName, bbox, bboxCrs ?? "EPSG:4326", fids, count, startIndex,
            internalFormat);
        if (!string.IsNullOrEmpty(filterXml))
        {
            var filter = WfsLocalFilter.ParseFesFilter(filterXml!);
            var sql = TranslateFilter(filter, gpkg, layer, bbox);
            if (sql == null)
                return EvaluateFilterHere(gpkg, query, filter, layer);

            var filtered = query with
            {
                Where = sql.Where,
                Bbox = sql.Bbox ?? query.Bbox,
                BboxSrs = sql.Bbox != null ? sql.BboxCrs : query.BboxSrs
            };
            try
            {
                return QueryPage(gpkg, filtered, layer);
            }
            catch (DdbException ex)
            {
                _logger.LogWarning(ex, "WFS GetFeature: DroneDB could not evaluate {Where}; filtering in the Registry",
                    filtered.Where);
                return EvaluateFilterHere(gpkg, query, filter, layer);
            }
        }

        return QueryPage(gpkg, query, layer);
    }

    /// <summary>The page <paramref name="query"/> selects; GML with numberMatched from DroneDB's count.</summary>
    private string QueryPage(string gpkg, VectorQuery query, OgcLayerDto layer)
    {
        var raw = DdbWrapper.QueryVector(gpkg, query);
        if (query.OutputFormat != "gml") return raw;
        return WfsFeatureCollectionWriter.ToFeatureCollection(raw, layer.Name, DdbWrapper.CountVector(gpkg, query));
    }

    /// <summary>
    /// The SQL form of <paramref name="filter"/> for the layer, or null when DroneDB cannot evaluate it
    /// (see <see cref="FesSqlTranslator"/>) or it has a BBOX while the request has one too.
    /// </summary>
    private FesSqlFilter? TranslateFilter(XmlElement filter, string gpkg, OgcLayerDto layer, double[]? bbox)
    {
        WfsLocalFilter.ValidateValueReferences(filter);
        var fieldNames = (WfsLayerFields.Read(DdbWrapper, gpkg, layer) ?? [])
            .Select(f => f.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var sql = FesSqlTranslator.Translate(filter, fieldNames);
        return sql?.Bbox != null && bbox != null ? null : sql;
    }

    /// <summary>
    /// A filter DroneDB cannot evaluate: up to 10 000 features are read as GML and filtered here,
    /// then <paramref name="query"/>'s page of the matches is returned in its format.
    /// </summary>
    private string EvaluateFilterHere(string gpkg, VectorQuery query, XmlElement filter, OgcLayerDto layer)
    {
        var all = DdbWrapper.QueryVector(gpkg, query with { MaxFeatures = 10000, StartIndex = 0, OutputFormat = "gml" });
        var collection = new XmlDocument();
        collection.LoadXml(WfsFeatureCollectionWriter.ToFeatureCollection(all, layer.Name));
        var matches = WfsLocalFilter.FilterMembers(collection, filter);

        if (query.OutputFormat == "gml")
            return WfsLocalFilter.PageMembers(collection, matches, query.StartIndex, query.MaxFeatures);

        // Other formats are written by DroneDB: read the page of matches by feature id
        var page = matches.Skip(query.StartIndex).Take(query.MaxFeatures)
            .Select(m => WfsFeatureIds.TryParse(WfsLocalFilter.FeatureOf(m)?.GetAttribute("id", NsGml), out _, out var fid) ? fid : -1)
            .Where(fid => fid >= 0)
            .ToArray();
        return DdbWrapper.QueryVector(gpkg, query with
        {
            Fids = page.Length > 0 ? page : [-1],
            StartIndex = 0
        });
    }


    /// <summary>An OGR GML response without features.</summary>
    private const string EmptyOgrCollection =
        "<ogr:FeatureCollection xmlns:ogr=\"http://ogr.maptools.org/\" xmlns:gml=\"" + NsGml + "\"/>";

    /// <summary>The fids of the feature ids that belong to <paramref name="layer"/>'s feature type.</summary>
    private static long[] FeatureIdsOf(IEnumerable<string> ids, OgcLayerDto layer)
    {
        var featureType = OgcNames.ToNcName(layer.Name);
        return ids
            .Select(id => WfsFeatureIds.TryParse(id, out var type, out var fid) && type == featureType ? fid : -1)
            .Where(fid => fid >= 0)
            .Distinct()
            .ToArray();
    }

    private static bool IsSupportedCrs(string crs)
    {
        var c = crs.Trim();
        return c.Equals("EPSG:4326", StringComparison.OrdinalIgnoreCase)
            || c.Equals("urn:ogc:def:crs:EPSG::4326", StringComparison.OrdinalIgnoreCase)
            || c.Equals("http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase)
            || c.Equals("CRS:84", StringComparison.OrdinalIgnoreCase)
            || c.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetPropertyValueAsync(string orgSlug, string dsSlug, string typeName,
        string valueReference, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(valueReference))
            throw new OgcException("InvalidParameterValue", "valueReference is required", 400, "valueReference");
        var fc = await GetFeatureAsync(orgSlug, dsSlug, typeName, null, null, count, startIndex,
            "application/gml+xml; version=3.2", resourceId, filterXml, ct);
        return TransformToValueCollection(fc, valueReference);
    }

    /// <summary>Transforms a wfs:FeatureCollection (string) into a wfs:ValueCollection
    /// where each wfs:member contains the value of the requested property reference.
    /// Supported references: "@gml:id", local property name, prefixed "ddb:foo".</summary>
    private static string TransformToValueCollection(string fcXml, string valueReference)
    {
        const string nsWfs = "http://www.opengis.net/wfs/2.0";
        const string nsGml = "http://www.opengis.net/gml/3.2";
        var doc = new XmlDocument();
        doc.LoadXml(fcXml);
        var root = doc.DocumentElement;
        var ts = DateTime.UtcNow.ToString("o");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<wfs:ValueCollection xmlns:wfs=\"").Append(nsWfs)
          .Append("\" xmlns:gml=\"").Append(nsGml)
          .Append("\" xmlns:ddb=\"").Append(NsDdb)
          .Append("\" timeStamp=\"").Append(ts).Append('\"');
        int returned = 0;
        var members = new List<string>();
        if (root != null)
        {
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child is not XmlElement m) continue;
                if (m.LocalName != "member" || m.NamespaceURI != nsWfs) continue;
                XmlElement? feat = null;
                foreach (XmlNode fc in m.ChildNodes)
                    if (fc is XmlElement fe) { feat = fe; break; }
                if (feat == null) continue;
                var value = ResolvePropertyValue(feat, valueReference);
                if (value == null) continue;
                members.Add(value);
                returned++;
            }
        }
        sb.Append(" numberMatched=\"").Append(returned)
          .Append("\" numberReturned=\"").Append(returned).Append("\">");
        foreach (var v in members)
        {
            sb.Append("<wfs:member>")
              .Append(System.Security.SecurityElement.Escape(v))
              .Append("</wfs:member>");
        }
        sb.Append("</wfs:ValueCollection>");
        return sb.ToString();
    }

    private static string? ResolvePropertyValue(XmlElement feat, string valueRef)
    {
        var r = valueRef.Trim();
        if (r.StartsWith('@'))
        {
            var name = r[1..];
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                var prefix = name[..colon];
                var local = name[(colon + 1)..];
                var ns = feat.GetNamespaceOfPrefix(prefix) ?? "";
                var v = feat.GetAttribute(local, ns);
                return string.IsNullOrEmpty(v) ? null : v;
            }
            var v2 = feat.GetAttribute(name);
            return string.IsNullOrEmpty(v2) ? null : v2;
        }
        var c2 = r.IndexOf(':');
        string lp = c2 > 0 ? r[(c2 + 1)..] : r;
        foreach (XmlNode n in feat.ChildNodes)
        {
            if (n is not XmlElement e) continue;
            if (string.Equals(e.LocalName, lp, StringComparison.Ordinal))
                return e.InnerText;
        }
        return null;
    }

    public async Task<string> GetFeatureByIdAsync(string orgSlug, string dsSlug, string id, string outputFormat,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new OgcException("MissingParameterValue", "id is required", 400, "id");

        // "<feature type>.<fid>": one primary-key read in the layer(s) of that feature type
        // (two layer names can map to the same NCName)
        if (WfsFeatureIds.TryParse(id, out var featureType, out var fid))
        {
            var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, ct: ct);
            var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
            var internalFormat = MapWfsOutputFormat(outputFormat);
            foreach (var l in layers.Where(l =>
                         l.EntryType == EntryType.Vector && OgcNames.ToNcName(l.Name) == featureType))
            {
                if (internalFormat != "gml") continue; // GeoJSON path: not supported for byId
                string raw;
                try
                {
                    raw = DdbWrapper.QueryVector(ResolveVectorArtifact(ddb, l),
                        new VectorQuery(l.InnerLayerName, Fids: [fid], MaxFeatures: 1, OutputFormat: internalFormat));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WFS GetFeatureById: skipping layer {Layer}", l.InnerLayerName);
                    continue;
                }

                var feature = WfsFeatureCollectionWriter.ToSingleFeature(raw, l.Name);
                if (feature != null) return feature;
            }
        }

        // Not found: per ETS WFS 2.0, GetFeatureById with unknown identifier returns HTTP 404.
        throw new OgcException("NotFound", $"No feature found with id '{id}'", 404, "id");
    }

    /// <summary>Maps a WFS-style output format (e.g. "application/gml+xml; version=3.2") to the
    /// internal DDB vector_query format token ("gml" / "geojson").</summary>
    private static string MapWfsOutputFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return "gml";
        var f = format.ToLowerInvariant();
        if (f.Contains("gml") || f.Contains("subtype=gml") || f.Contains("text/xml")) return "gml";
        if (f.Contains("json")) return "geojson";
        return "gml";
    }
}
