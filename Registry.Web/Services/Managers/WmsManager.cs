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
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using SkiaSharp;
using Registry.Web.Services.Caching;

namespace Registry.Web.Services.Managers;

/// <summary>
/// WMS 1.3.0 (and best-effort 1.1.1) manager.
/// </summary>
public class WmsManager : OgcManagerBase, IWmsManager
{
    private static readonly string[] Requests = ["GetCapabilities", "GetMap", "GetFeatureInfo"];

    private readonly ILogger<WmsManager> _logger;
    private readonly ICacheManager _cacheManager;
    private readonly INativeWorkGate _gate;
    private readonly IRasterIndexCatalog _indexes;

    public WmsManager(IDatasetAccessResolver access, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IRecordCache cache, IHttpContextAccessor ctx, ILogger<WmsManager> logger,
        ICacheManager cacheManager, IRasterIndexCatalog indexes, INativeWorkGate? gate = null)
        : base(access, ar, w, c, cache, ctx)
    {
        _logger = logger;
        _cacheManager = cacheManager;
        _indexes = indexes;
        _gate = gate ?? Services.Adapters.NativeWorkGate.Default;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string version,
        string? folderPath = null, CancellationToken ct = default)
    {
        var key = await LayerCatalog.CacheKeyAsync(orgSlug, dsSlug, $"caps-wms-v2-{version}", folderPath, ct);
        return await Records.GetOrCreateRecordAsync(key, async token =>
        {

            var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, token);
            var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath, ct: token);
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
                await w.WriteAttributeStringAsync("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                    version == "1.3.0"
                        ? "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd"
                        : "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.1.1/capabilities_1_1_1.xsd");

                w.WriteStartElement("Service");
                w.WriteElementString("Name", "WMS");
                w.WriteElementString("Title", $"DroneDB WMS - {orgSlug}/{dsSlug}");
                w.WriteElementString("Abstract", $"OGC Web Map Service for dataset {orgSlug}/{dsSlug}");
                w.WriteStartElement("OnlineResource");
                await w.WriteAttributeStringAsync("xlink", "type", NsXlink, "simple");
                await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?");
                await w.WriteEndElementAsync(); // OnlineResource
                await w.WriteEndElementAsync(); // Service

                w.WriteStartElement("Capability");
                w.WriteStartElement("Request");
                foreach (var req in Requests)
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

                    // Always advertise the default style. A spectral-index style is advertised per layer only when
                    // libddb lists it for the raster (IRasterIndexCatalog: the same list GetMap enforces), so the
                    // advertised contract matches what the server accepts (CITE "each-style" tests would
                    // otherwise fail on RGB orthophotos).
                    w.WriteStartElement("Style");
                    w.WriteElementString("Name", "default");
                    w.WriteElementString("Title", "Default natural color");
                    await w.WriteEndElementAsync();

                    foreach (var idx in await GetIndexesAsync(ddb, layer, token))
                    {
                        w.WriteStartElement("Style");
                        w.WriteElementString("Name", idx);
                        w.WriteElementString("Title", $"{idx} spectral index");
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
            return xml;
        }, CapabilitiesCache, ct);
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
        double[] bbox, string crs, int width, int height, string format, string? bgColor, bool transparent,
        CancellationToken ct = default)
    {
        // Defense-in-depth: re-validate request-level constraints. The controller validates these
        // before BBOX parsing, but the manager guarantees the same contract for any future caller.
        // Validation errors MUST surface as proper OGC exceptions, so they run outside the safety net.
        WmsValidator.ValidateLayers(layers);
        WmsValidator.ValidateDimensions(width, height);
        WmsValidator.ValidateCrs(crs);
        WmsValidator.ValidateMapFormat(format);

        try
        {
            return await GetMapAsyncCore(orgSlug, dsSlug, layers, styles, bbox, crs, width, height, format, bgColor,
                transparent, ct);
        }
        catch (OgcException)
        {
            // Preserve protocol-level exceptions (LayerNotDefined, StyleNotDefined, etc.) so the
            // OgcExceptionFilter can return the proper ServiceExceptionReport XML.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // WMS 1.3 §7.3.3.11: GetMap MUST return an image when parameters are syntactically valid.
            // Under load, native (GDAL/Skia) failures or transient I/O issues can bubble up past the
            // per-layer try/catch; we never want a 500 here, so synthesize a fully transparent (or
            // background-colored) image of the requested size/format as a last-resort fallback.
            _logger.LogError(ex, "WMS GetMap safety-net fallback triggered for layers=[{L}] bbox=[{B}] crs={Crs} {W}x{H} fmt={F}",
                string.Join(',', layers ?? []), string.Join(',', bbox), crs, width, height, format);
            return BuildFallbackImage(width, height, format, transparent, bgColor);
        }
    }

    private async Task<byte[]> GetMapAsyncCore(string orgSlug, string dsSlug, string[] layers, string[] styles,
        double[] bbox, string crs, int width, int height, string format, string? bgColor, bool transparent,
        CancellationToken ct)
    {
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var rasters = await ResolveRasterLayersAsync(orgSlug, dsSlug, ddb, layers, styles, ct);

        // Identical (and sub-pixel jittered) requests share one render; the render uses the
        // snapped BBOX so that a cached image is what a fresh render returns
        var renderKey = WmsRenderKey.Build(bbox, crs, width, height, format, transparent, bgColor,
            rasters.Select(r => (r.Layer.EntryHash, r.Style)));
        try
        {
            // The render runs with the cache flight's token: it stops when every request for it has gone
            return await _cacheManager.GetAsync(MagicStrings.OgcRenderCacheSeed,
                CacheCategories.ForDataset(orgSlug, dsSlug), ct, renderKey.Key,
                new Func<CancellationToken, Task<byte[]>>(token => _gate.RunAsync(NativeLane.Tiles, () =>
                    Render(ddb, rasters, renderKey.Bbox, crs, width, height, format, bgColor, transparent), token)));
        }
        catch (DegradedRenderException ex)
        {
            // Served once, never cached: the next request renders again
            return ex.Image;
        }
    }

    /// <summary>The spectral indices libddb renders from a raster layer (the source it reads), empty when none.</summary>
    private Task<IReadOnlyList<string>> GetIndexesAsync(IDDB ddb, OgcLayerDto layer, CancellationToken ct) =>
        _indexes.GetAsync(layer.EntryHash, ResolveRasterSource(ddb, layer), ct);

    /// <summary>
    /// The raster layers of LAYERS in drawing order with their style: empty for the natural render, else the
    /// spectral index as libddb names it. Unknown layers and styles are protocol errors (raised before any render
    /// or cache lookup); non-raster layers are skipped.
    /// </summary>
    private async Task<List<(OgcLayerDto Layer, string Style)>> ResolveRasterLayersAsync(string orgSlug,
        string dsSlug, IDDB ddb, string[] layers, string[] styles, CancellationToken ct)
    {
        var rasters = new List<(OgcLayerDto Layer, string Style)>();
        for (var idx = 0; idx < layers.Length; idx++)
        {
            var name = layers[idx];
            var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, ct: ct);
            if (layer == null)
                throw new OgcException("LayerNotDefined", $"Layer '{name}' not found", 404, "LAYERS");
            if (layer.EntryType != EntryType.GeoRaster)
                continue;

            // STYLES contract: "" / "default" are the natural render; any other style must be a spectral index
            // libddb lists for this raster, the list GetCapabilities advertises for the layer
            var requested = (styles != null && idx < styles.Length) ? styles[idx] : "";
            var style = WmsValidator.IsDefaultStyle(requested)
                ? string.Empty
                : WmsValidator.ResolveIndexStyle(requested, name, await GetIndexesAsync(ddb, layer, ct));

            rasters.Add((layer, style));
        }

        if (rasters.Count == 0)
            throw new OgcException("LayerNotDefined", "No renderable raster layers", 404, "LAYERS");
        return rasters;
    }

    private byte[] Render(IDDB ddb, List<(OgcLayerDto Layer, string Style)> rasters, double[] bbox, string crs,
        int width, int height, string format, string? bgColor, bool transparent)
    {
        var (_, mime) = MapFormat(format);

        // One layer whose native render is already the requested image: no decode, composite
        // and re-encode in managed code
        if (rasters.Count == 1 && NativeRenderMatches(mime, bgColor, transparent))
        {
            try
            {
                var bytes = RenderLayer(ddb, rasters[0], bbox, crs, width, height, mime);
                if (bytes.Length > 0)
                    return bytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "WMS layer '{Layer}' native render failed for bbox=[{B}] crs={Crs} {W}x{H}; compositing instead",
                    rasters[0].Layer.Name, string.Join(',', bbox), crs, width, height);
            }
        }

        return Composite(ddb, rasters, bbox, crs, width, height, format, bgColor, transparent);
    }

    /// <summary>
    /// True when the native render of a single layer equals the composited image: PNG/WebP keep
    /// the layer's transparency (TRANSPARENT=TRUE); JPEG has none and the native renderer fills
    /// nodata with white, the default BGCOLOR.
    /// </summary>
    private static bool NativeRenderMatches(string mime, string? bgColor, bool transparent)
    {
        if (mime == "image/jpeg")
            return !transparent && ParseBackground(bgColor) == SKColors.White;
        return transparent;
    }

    private byte[] RenderLayer(IDDB ddb, (OgcLayerDto Layer, string Style) raster, double[] bbox, string crs,
        int width, int height, string mime)
    {
        var src = ResolveRasterArtifact(ddb, raster.Layer);
        return raster.Style.Length > 0
            ? DdbWrapper.RenderRasterIndex(src, raster.Style, bbox, crs, width, height, mime)
            : DdbWrapper.RenderRasterRegion(src, bbox, crs, width, height, mime);
    }

    /// <summary>
    /// Renders each layer in declaration order and alpha-blends them top-down with SkiaSharp.
    /// Throws <see cref="DegradedRenderException"/> (carrying the image) when a layer failed.
    /// </summary>
    private byte[] Composite(IDDB ddb, List<(OgcLayerDto Layer, string Style)> rasters, double[] bbox, string crs,
        int width, int height, string format, string? bgColor, bool transparent)
    {
        var failed = false;
        using var canvas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var surface = SKSurface.Create(canvas.Info, canvas.GetPixels(), canvas.RowBytes))
        {
            surface.Canvas.Clear(transparent ? SKColors.Transparent : ParseBackground(bgColor));

            foreach (var raster in rasters)
            {
                // Per-layer render is wrapped so failures (e.g. BBOX with no overlap on a
                // multispectral source, GDAL warp errors on degenerate windows) do not
                // surface as HTTP 500. The WMS 1.3 spec requires GetMap to return a valid
                // image whenever parameters are syntactically valid; a layer that cannot be
                // rendered for the requested area is simply drawn as fully transparent.
                byte[]? layerBytes = null;
                try
                {
                    layerBytes = RenderLayer(ddb, raster, bbox, crs, width, height, "image/png");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = true;
                    _logger.LogWarning(ex, "WMS layer '{Layer}' render failed for bbox=[{B}] crs={Crs} {W}x{H}; treating as transparent",
                        raster.Layer.Name, string.Join(',', bbox), crs, width, height);
                }

                if (layerBytes == null || layerBytes.Length == 0)
                    continue;

                using var bmp = SKBitmap.Decode(layerBytes);
                if (bmp == null)
                    continue;
                using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
                surface.Canvas.DrawBitmap(bmp, new SKRect(0, 0, width, height), paint);
            }

            surface.Canvas.Flush();
        }

        // Encode to requested format.
        using var image = SKImage.FromBitmap(canvas);
        var (skFormat, _) = MapFormat(format);
        using var data = image.Encode(skFormat, 90);
        var result = data.ToArray();
        return failed ? throw new DegradedRenderException(result) : result;
    }

    /// <summary>A GetMap image rendered with a layer missing: returned to the client, not cached.</summary>
    private sealed class DegradedRenderException(byte[] image) : Exception("A WMS layer failed to render")
    {
        public byte[] Image { get; } = image;
    }

    private static SKColor ParseBackground(string? bg)
    {
        if (string.IsNullOrWhiteSpace(bg)) return SKColors.White;
        var s = bg.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
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

    /// <summary>Builds a single-color image (transparent or BGCOLOR) of the requested size/format.
    /// Used as a last-resort fallback when an unexpected exception escapes <see cref="GetMapAsyncCore"/>.</summary>
    private static byte[] BuildFallbackImage(int width, int height, string format, bool transparent, string? bgColor)
    {
        // Clamp to a sane minimum so SkiaSharp never sees a zero/negative dimension.
        var w = Math.Max(1, Math.Min(width, 4096));
        var h = Math.Max(1, Math.Min(height, 4096));
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var surface = SKSurface.Create(bmp.Info, bmp.GetPixels(), bmp.RowBytes))
        {
            surface.Canvas.Clear(transparent ? SKColors.Transparent : ParseBackground(bgColor));
            surface.Canvas.Flush();
        }
        using var image = SKImage.FromBitmap(bmp);
        var (skFormat, _) = MapFormat(format);
        using var data = image.Encode(skFormat, 90);
        return data.ToArray();
    }

    public async Task<string> GetFeatureInfoAsync(string orgSlug, string dsSlug, string layerName,
        double[] bbox, string crs, int width, int height, int i, int j, string infoFormat,
        CancellationToken ct = default)
    {
        if (i < 0 || i >= width || j < 0 || j >= height)
            throw new OgcException("InvalidPoint", "i/j out of bounds", 400);
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, layerName, ct: ct)
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

        return infoFormat.Contains("xml", StringComparison.OrdinalIgnoreCase) ?
            $"<FeatureInfoResponse>{System.Net.WebUtility.HtmlEncode(json)}</FeatureInfoResponse>" :
            json;
    }
}

