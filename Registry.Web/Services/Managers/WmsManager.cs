using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
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
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx, ILogger<WmsManager> logger)
        : base(u, a, d, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string version,
        string? folderPath = null)
    {
        var key = $"ogc-caps-wms-v2-{version}-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);
        var rasterLayers = layers.Where(l => l.EntryType == EntryType.GeoRaster).ToList();
        var totalBbox = AggregateBbox(rasterLayers);
        var baseUrl = GetServiceUrl(orgSlug, dsSlug, "wms", folderPath);

        var sb = new StringBuilder();
        await using (var w = CreateXmlWriter(sb))
        {
            await w.WriteStartElementAsync(null, "WMS_Capabilities", NsWms);
            w.WriteAttributeString("version", version);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
            await w.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                version == "1.3.0"
                    ? "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd"
                    : "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.1.1/capabilities_1_1_1.xsd");

            w.WriteStartElement("Service");
            w.WriteElementString("Name", "WMS");
            w.WriteElementString("Title", $"DroneDB WMS — {orgSlug}/{dsSlug}");
            w.WriteElementString("Abstract", $"OGC Web Map Service for dataset {orgSlug}/{dsSlug}");
            w.WriteStartElement("OnlineResource");
            w.WriteAttributeString("xlink", "type", NsXlink, "simple");
            w.WriteAttributeString("xlink", "href", NsXlink, baseUrl + "?");
            w.WriteEndElement(); // OnlineResource
            await w.WriteEndElementAsync(); // Service

            w.WriteStartElement("Capability");
            w.WriteStartElement("Request");
            foreach (var req in new[] { "GetCapabilities", "GetMap", "GetFeatureInfo" })
            {
                w.WriteStartElement(req);
                w.WriteElementString("Format", req == "GetMap" ? "image/png" : "text/xml");
                WriteWmsDcp(w, baseUrl);
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

                // Always advertise the default style. Spectral-index styles (NDVI/NDRE/NDWI/EVI/SAVI)
                // are advertised per-layer only when the underlying raster is multispectral, so the
                // advertised contract matches what the server enforces in GetMap (CITE "each-style"
                // tests would otherwise fail on RGB orthophotos).
                w.WriteStartElement("Style");
                w.WriteElementString("Name", "default");
                w.WriteElementString("Title", "Default natural color");
                await w.WriteEndElementAsync();

                if (layer.IsMultispectral)
                {
                    foreach (var idx in Utilities.Ogc.WmsValidator.SpectralIndexes)
                    {
                        w.WriteStartElement("Style");
                        w.WriteElementString("Name", idx);
                        w.WriteElementString("Title", $"{idx} spectral index");
                        await w.WriteEndElementAsync();
                    }
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
        // Defense-in-depth: re-validate request-level constraints. The controller validates these
        // before BBOX parsing, but the manager guarantees the same contract for any future caller.
        Utilities.Ogc.WmsValidator.ValidateLayers(layers);
        Utilities.Ogc.WmsValidator.ValidateDimensions(width, height);
        Utilities.Ogc.WmsValidator.ValidateCrs(crs);
        Utilities.Ogc.WmsValidator.ValidateMapFormat(format);

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

                // STYLES contract: "" / "default" → natural render. Spectral indexes
                // (NDVI/NDRE/NDWI/EVI/SAVI) are server-defined styles that are only
                // meaningful on multispectral rasters; reject them otherwise so the
                // accepted contract matches what GetCapabilities advertises per-layer.
                if (!string.IsNullOrEmpty(style)
                    && !string.Equals(style, "default", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsSpectralIndex(style))
                    {
                        if (!layer.IsMultispectral)
                            throw new OgcException("StyleNotDefined",
                                $"Spectral style '{style}' is not defined for layer '{name}' (not multispectral)",
                                400, "STYLES");
                    }
                    else
                    {
                        throw new OgcException("StyleNotDefined",
                            $"Style '{style}' is not defined", 400, "STYLES");
                    }
                }

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

