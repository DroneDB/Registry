using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using SkiaSharp;

namespace Registry.Web.Services.Managers;

/// <summary>WMS 1.3.0 (and best-effort 1.1.1) manager.</summary>
public class WmsManager : OgcManagerBase, IWmsManager
{
    private readonly ILogger<WmsManager> _logger;

    public WmsManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, ILogger<WmsManager> logger)
        : base(u, a, d, ar, w, c, cache)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string version,
        string? folderPath = null)
    {
        var key = $"ogc-caps-wms-{version}-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);
        var rasterLayers = layers.Where(l => l.EntryType == EntryType.GeoRaster).ToList();
        var totalBbox = AggregateBbox(rasterLayers);

        var sb = new StringBuilder();
        await using (var w = CreateXmlWriter(sb))
        {
            await w.WriteStartElementAsync(null, "WMS_Capabilities", NsWms);
            w.WriteAttributeString("version", version);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);

            w.WriteStartElement("Service");
            w.WriteElementString("Name", "WMS");
            w.WriteElementString("Title", $"DroneDB WMS — {orgSlug}/{dsSlug}");
            await w.WriteEndElementAsync();

            w.WriteStartElement("Capability");
            w.WriteStartElement("Request");
            foreach (var req in new[] { "GetCapabilities", "GetMap", "GetFeatureInfo" })
            {
                w.WriteStartElement(req);
                w.WriteElementString("Format", req == "GetMap" ? "image/png" : "text/xml");
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync(); // Request

            w.WriteStartElement("Exception");
            w.WriteElementString("Format", "XML");
            await w.WriteEndElementAsync();

            // Aggregated root layer
            w.WriteStartElement("Layer");
            w.WriteAttributeString("queryable", "1");
            w.WriteElementString("Title", $"{orgSlug}/{dsSlug}");
            w.WriteElementString(version == "1.3.0" ? "CRS" : "SRS", "EPSG:4326");
            w.WriteElementString(version == "1.3.0" ? "CRS" : "SRS", "EPSG:3857");
            w.WriteElementString(version == "1.3.0" ? "CRS" : "SRS", "CRS:84");

            if (totalBbox != null)
                WriteBbox(w, version, totalBbox);

            foreach (var layer in rasterLayers)
            {
                w.WriteStartElement("Layer");
                w.WriteAttributeString("queryable", "1");
                w.WriteElementString("Name", layer.Name);
                w.WriteElementString("Title", layer.Title);
                if (layer.BboxWgs84 != null)
                    WriteBbox(w, version, layer.BboxWgs84);

                // Advertise spectral-index styles (server-side band math).
                foreach (var styleName in new[] { "default", "NDVI", "NDRE", "NDWI", "EVI", "SAVI" })
                {
                    w.WriteStartElement("Style");
                    w.WriteElementString("Name", styleName);
                    w.WriteElementString("Title", styleName == "default" ? "Default natural color" : styleName);
                    await w.WriteEndElementAsync();
                }
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync(); // root Layer
            await w.WriteEndElementAsync(); // Capability
            await w.WriteEndElementAsync(); // WMS_Capabilities
            await w.WriteEndDocumentAsync();
        }

        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    private static void WriteBbox(XmlWriter w, string version, double[] bbox)
    {
        if (version == "1.3.0")
        {
            w.WriteStartElement("EX_GeographicBoundingBox");
            w.WriteElementString("westBoundLongitude", Fmt(bbox[0]));
            w.WriteElementString("eastBoundLongitude", Fmt(bbox[2]));
            w.WriteElementString("southBoundLatitude", Fmt(bbox[1]));
            w.WriteElementString("northBoundLatitude", Fmt(bbox[3]));
            w.WriteEndElement();
            w.WriteStartElement("BoundingBox");
            w.WriteAttributeString("CRS", "EPSG:4326");
            // WMS 1.3.0 EPSG:4326 = lat,lon
            w.WriteAttributeString("minx", Fmt(bbox[1]));
            w.WriteAttributeString("miny", Fmt(bbox[0]));
            w.WriteAttributeString("maxx", Fmt(bbox[3]));
            w.WriteAttributeString("maxy", Fmt(bbox[2]));
        }
        else
        {
            w.WriteStartElement("LatLonBoundingBox");
            w.WriteAttributeString("minx", Fmt(bbox[0]));
            w.WriteAttributeString("miny", Fmt(bbox[1]));
            w.WriteAttributeString("maxx", Fmt(bbox[2]));
            w.WriteAttributeString("maxy", Fmt(bbox[3]));
        }

        w.WriteEndElement();
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public async Task<byte[]> GetMapAsync(string orgSlug, string dsSlug, string[] layers, string[] styles,
        double[] bbox, string crs, int width, int height, string format, string? bgColor, bool transparent)
    {
        if (layers == null || layers.Length == 0)
            throw new OgcException("MissingParameterValue", "LAYERS is required", 400, "LAYERS");
        if (width < 1 || width > 4096 || height < 1 || height > 4096)
            throw new OgcException("InvalidParameterValue", "WIDTH/HEIGHT must be 1..4096", 400, "WIDTH");

        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);

        // Render each layer in declaration order, then alpha-blend top-down with SkiaSharp.
        // Each per-layer render is decoded as RGBA8888 to a transparent canvas.
        using var canvas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var surface = SKSurface.Create(canvas.Info, canvas.GetPixels(), canvas.RowBytes))
        {
            // Background fill
            if (!transparent)
            {
                var bg = ParseBackground(bgColor);
                surface.Canvas.Clear(bg);
            }
            else
            {
                surface.Canvas.Clear(SKColors.Transparent);
            }

            var anyRendered = false;
            for (var idx = 0; idx < layers.Length; idx++)
            {
                var name = layers[idx];
                var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name);
                if (layer == null)
                    throw new OgcException("LayerNotDefined", $"Layer '{name}' not found", 404, "LAYERS");
                if (layer.EntryType != EntryType.GeoRaster)
                    continue;

                var src = ResolveRasterArtifact(ddb, layer);
                var style = (styles != null && idx < styles.Length) ? styles[idx] : "";
                var layerBytes = IsSpectralIndex(style)
                    ? DdbWrapper.RenderRasterIndex(src, style.ToUpperInvariant(), bbox, crs, width, height, "image/png")
                    : DdbWrapper.RenderRasterRegion(src, bbox, crs, width, height, "image/png");

                using var bmp = SKBitmap.Decode(layerBytes);
                if (bmp == null) continue;
                using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
                surface.Canvas.DrawBitmap(bmp, new SKRect(0, 0, width, height), paint);
                anyRendered = true;
            }

            if (!anyRendered)
                throw new OgcException("LayerNotDefined", "No renderable raster layers", 404, "LAYERS");

            surface.Canvas.Flush();
        }

        // Encode to requested format.
        using var image = SKImage.FromBitmap(canvas);
        var (skFormat, _) = MapFormat(format);
        using var data = image.Encode(skFormat, 90);
        return data.ToArray();
    }

    private static bool IsSpectralIndex(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return false;
        var s = style.ToUpperInvariant();
        return s is "NDVI" or "NDRE" or "NDWI" or "EVI" or "SAVI";
    }

    private static SKColor ParseBackground(string? bg)
    {
        if (string.IsNullOrWhiteSpace(bg)) return SKColors.White;
        var s = bg.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return new SKColor((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }
        return SKColors.White;
    }

    private static (SKEncodedImageFormat, string) MapFormat(string mime)
    {
        return (mime ?? "image/png").ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" or "jpg" or "jpeg" => (SKEncodedImageFormat.Jpeg, "image/jpeg"),
            "image/webp" or "webp" => (SKEncodedImageFormat.Webp, "image/webp"),
            _ => (SKEncodedImageFormat.Png, "image/png"),
        };
    }

    public async Task<string> GetFeatureInfoAsync(string orgSlug, string dsSlug, string layerName,
        double[] bbox, string crs, int width, int height, int i, int j, string infoFormat)
    {
        if (i < 0 || i >= width || j < 0 || j >= height)
            throw new OgcException("InvalidPoint", "i/j out of bounds", 400);
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, layerName)
                    ?? throw new OgcException("LayerNotDefined", $"Layer '{layerName}' not found", 404, "QUERY_LAYERS");
        if (layer.EntryType != EntryType.GeoRaster)
            throw new OgcException("OperationNotSupported", "GetFeatureInfo only supports raster layers", 400);

        // Pixel → geo (axis order already normalized by parser).
        var dx = (bbox[2] - bbox[0]) / width;
        var dy = (bbox[3] - bbox[1]) / height;
        var geoX = bbox[0] + (i + 0.5) * dx;
        var geoY = bbox[3] - (j + 0.5) * dy;

        var src = ResolveRasterArtifact(ddb, layer);
        var json = DdbWrapper.QueryRasterPoint(src, geoX, geoY, crs);

        if (infoFormat.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return $"<FeatureInfoResponse>{System.Net.WebUtility.HtmlEncode(json)}</FeatureInfoResponse>";
        }
        return json;
    }
}

/// <summary>WFS 2.0.0 manager (BBOX filter + pagination, no CQL).</summary>
public class WfsManager : OgcManagerBase, IWfsManager
{
    private readonly ILogger<WfsManager> _logger;

    public WfsManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, ILogger<WfsManager> logger)
        : base(u, a, d, ar, w, c, cache)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null)
    {
        var key = $"ogc-caps-wfs-2.0.0-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);
        var vectorLayers = layers.Where(l => l.EntryType == EntryType.Vector).ToList();

        var sb = new StringBuilder();
        await using (var w = CreateXmlWriter(sb))
        {
            await w.WriteStartElementAsync("wfs", "WFS_Capabilities", NsWfs);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
            await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
            w.WriteAttributeString("version", "2.0.0");

            await w.WriteStartElementAsync("ows", "ServiceIdentification", null);
            await w.WriteElementStringAsync("ows", "Title", null, $"DroneDB WFS — {orgSlug}/{dsSlug}");
            await w.WriteElementStringAsync("ows", "ServiceType", null, "WFS");
            await w.WriteElementStringAsync("ows", "ServiceTypeVersion", null, "2.0.0");
            await w.WriteEndElementAsync();

            w.WriteStartElement("wfs", "FeatureTypeList", NsWfs);
            foreach (var l in vectorLayers)
            {
                w.WriteStartElement("wfs", "FeatureType", NsWfs);
                w.WriteElementString("wfs", "Name", NsWfs, l.Name);
                w.WriteElementString("wfs", "Title", NsWfs, l.Title);
                w.WriteElementString("wfs", "DefaultCRS", NsWfs, "urn:ogc:def:crs:EPSG::4326");
                if (l.BboxWgs84 != null)
                {
                    await w.WriteStartElementAsync("ows", "WGS84BoundingBox", null);
                    await w.WriteElementStringAsync("ows", "LowerCorner", null,
                        FormattableString.Invariant($"{l.BboxWgs84[0]} {l.BboxWgs84[1]}"));
                    await w.WriteElementStringAsync("ows", "UpperCorner", null,
                        FormattableString.Invariant($"{l.BboxWgs84[2]} {l.BboxWgs84[3]}"));
                    await w.WriteEndElementAsync();
                }
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
        }
        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    public async Task<string> DescribeFeatureTypeAsync(string orgSlug, string dsSlug, string[] typeNames)
    {
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);

        var sb = new StringBuilder();
        await using var w = CreateXmlWriter(sb);
        await w.WriteStartElementAsync("xsd", "schema", NsXsd);
        await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
        w.WriteAttributeString("elementFormDefault", "qualified");

        foreach (var name in typeNames ?? Array.Empty<string>())
        {
            var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name);
            if (layer == null || layer.EntryType != EntryType.Vector) continue;
            var gpkg = ResolveVectorArtifact(ddb, layer);
            var describeJson = DdbWrapper.DescribeVector(gpkg, layer.InnerLayerName);
            JObject describe;
            try { describe = JObject.Parse(describeJson); }
            catch { continue; }

            var layers = describe["layers"] as JArray;
            if (layers == null) continue;

            foreach (var ld in layers.OfType<JObject>())
            {
                var lname = ld["name"]?.Value<string>() ?? name;
                await w.WriteStartElementAsync("xsd", "complexType", null);
                w.WriteAttributeString("name", $"{lname}Type");
                await w.WriteStartElementAsync("xsd", "complexContent", null);
                await w.WriteStartElementAsync("xsd", "extension", null);
                w.WriteAttributeString("base", "gml:AbstractFeatureType");
                await w.WriteStartElementAsync("xsd", "sequence", null);

                await w.WriteStartElementAsync("xsd", "element", null);
                w.WriteAttributeString("name", "geom");
                w.WriteAttributeString("type", "gml:GeometryPropertyType");
                w.WriteAttributeString("minOccurs", "0");
                await w.WriteEndElementAsync();

                if (ld["fields"] is JArray fields)
                {
                    foreach (var f in fields.OfType<JObject>())
                    {
                        var fname = f["name"]?.Value<string>() ?? "field";
                        var ftype = (f["type"]?.Value<string>() ?? "String").ToLowerInvariant();
                        var xsdType = ftype switch
                        {
                            "integer" => "xsd:int",
                            "integer64" => "xsd:long",
                            "real" => "xsd:double",
                            "datetime" or "date" => "xsd:dateTime",
                            _ => "xsd:string"
                        };
                        await w.WriteStartElementAsync("xsd", "element", null);
                        w.WriteAttributeString("name", fname);
                        w.WriteAttributeString("type", xsdType);
                        w.WriteAttributeString("minOccurs", "0");
                        await w.WriteEndElementAsync();
                    }
                }

                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
            }
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    public async Task<string> GetFeatureAsync(string orgSlug, string dsSlug, string typeName,
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat)
    {
        if (count <= 0) count = 1000;
        count = Math.Clamp(count, 1, 10000);
        if (startIndex < 0) startIndex = 0;

        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, typeName)
                    ?? throw new OgcException("InvalidParameterValue", $"Unknown typeNames '{typeName}'", 404, "typeNames");
        if (layer.EntryType != EntryType.Vector)
            throw new OgcException("OperationNotSupported", "WFS supports only Vector layers", 400);

        var gpkg = ResolveVectorArtifact(ddb, layer);
        return DdbWrapper.QueryVector(gpkg, layer.InnerLayerName, bbox, bboxCrs ?? "EPSG:4326",
            count, startIndex, outputFormat);
    }
}
