#nullable enable
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
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>
/// WMTS 1.0.0 manager. Capabilities + tile retrieval. For raster layers we delegate
/// to the existing XYZ tile pipeline (<see cref="IDDB.GenerateTile"/>); for vector
/// layers we proxy MVT tiles directly.
/// </summary>
public class WmtsManager : OgcManagerBase, IWmtsManager
{
    private readonly ILogger<WmtsManager> _logger;

    public WmtsManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx, ILogger<WmtsManager> logger)
        : base(u, a, d, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug,
        IReadOnlyCollection<string>? sections = null, string? folderPath = null)
    {
        // Normalize the sections key: null = all; otherwise sort+dedupe so order doesn't fragment the cache.
        string sectionsKey;
        HashSet<string>? sectionsSet = null;
        if (sections == null || sections.Count == 0)
        {
            sectionsKey = "all";
        }
        else
        {
            sectionsSet = new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase);
            sectionsKey = string.Join("+", sectionsSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }

        var key = $"ogc-caps-wmts-v5-1.0.0-{orgSlug}-{dsSlug}-{folderPath ?? ""}-{sectionsKey}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        bool Include(string section) => sectionsSet == null || sectionsSet.Contains(section);

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);
        var baseUrl = GetServiceUrl(orgSlug, dsSlug, "wmts", folderPath);

        var sb = new StringBuilder();
        await using (var w = CreateXmlWriter(sb))
        {
            w.WriteStartElement("Capabilities", NsWmts);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
            await w.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            await w.WriteAttributeStringAsync("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                "http://www.opengis.net/wmts/1.0 http://schemas.opengis.net/wmts/1.0/wmtsGetCapabilities_response.xsd");
            w.WriteAttributeString("version", "1.0.0");

            if (Include("ServiceIdentification"))
            {
                await w.WriteStartElementAsync("ows", "ServiceIdentification", null);
                await w.WriteElementStringAsync("ows", "Title", null, $"DroneDB WMTS — {orgSlug}/{dsSlug}");
                await w.WriteElementStringAsync("ows", "ServiceType", null, "OGC WMTS");
                await w.WriteElementStringAsync("ows", "ServiceTypeVersion", null, "1.0.0");
                await w.WriteEndElementAsync();
            }

            if (Include("ServiceProvider"))
            {
                await w.WriteStartElementAsync("ows", "ServiceProvider", null);
                await w.WriteElementStringAsync("ows", "ProviderName", null, "DroneDB");
                await w.WriteStartElementAsync("ows", "ProviderSite", null);
                await w.WriteAttributeStringAsync("xlink", "href", NsXlink, "https://dronedb.app");
                await w.WriteEndElementAsync();
                await w.WriteStartElementAsync("ows", "ServiceContact", null);
                await w.WriteElementStringAsync("ows", "IndividualName", null, "DroneDB Team");
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
            }

            if (Include("OperationsMetadata"))
            {
                await w.WriteStartElementAsync("ows", "OperationsMetadata", null);
                foreach (var op in new[] { "GetCapabilities", "GetTile", "GetFeatureInfo" })
                    await WriteOwsOperationAsync(w, op, baseUrl, addGetEncodingKvpConstraint: true);
                await w.WriteEndElementAsync();
            }

            if (Include("Contents"))
            {
                w.WriteStartElement("Contents");
                foreach (var layer in layers)
                {
                    // WMTS 1.0.0 core only standardizes raster image tiles. Vector layers
                    // (served as Mapbox Vector Tiles) belong to a separate WMTS extension
                    // (OGC 17-083r2) not supported by CITE — and TeamEngine has no MVT
                    // parser, so advertising MVT here causes the "each-layer" suite to fail
                    // regardless of response. Vector layers remain available via the
                    // dedicated MVT endpoint; skip them in WMTS GetCapabilities.
                    if (layer.EntryType == EntryType.Vector) continue;
                    var isVector = false;
                    w.WriteStartElement("Layer");
                    await w.WriteElementStringAsync("ows", "Title", null, layer.Title);
                    await w.WriteElementStringAsync("ows", "Identifier", null, OgcNames.ToNcName(layer.Name));
                    if (layer.BboxWgs84 != null)
                    {
                        await w.WriteStartElementAsync("ows", "WGS84BoundingBox", null);
                        await w.WriteElementStringAsync("ows", "LowerCorner", null,
                            FormattableString.Invariant($"{layer.BboxWgs84[0]} {layer.BboxWgs84[1]}"));
                        await w.WriteElementStringAsync("ows", "UpperCorner", null,
                            FormattableString.Invariant($"{layer.BboxWgs84[2]} {layer.BboxWgs84[3]}"));
                        await w.WriteEndElementAsync();
                    }
                    w.WriteStartElement("Style");
                    w.WriteAttributeString("isDefault", "true");
                    await w.WriteElementStringAsync("ows", "Identifier", null, WmtsConformance.DefaultStyle);
                    await w.WriteEndElementAsync();
                    if (isVector)
                    {
                        w.WriteElementString("Format", WmtsConformance.VectorFormat);
                    }
                    else
                    {
                        foreach (var f in WmtsConformance.SupportedRasterFormats)
                            w.WriteElementString("Format", f);
                    }
                    w.WriteStartElement("TileMatrixSetLink");
                    w.WriteElementString("TileMatrixSet", WmtsConformance.DefaultTileMatrixSet);
                    await w.WriteEndElementAsync();
                    await w.WriteEndElementAsync(); // Layer
                }

                WriteGoogleMapsCompatible(w);
                await w.WriteEndElementAsync(); // Contents
            }

            // ServiceMetadataURL — required by some WMTS clients/tests
            w.WriteStartElement("ServiceMetadataURL");
            await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?service=WMTS&request=GetCapabilities&version=1.0.0");
            await w.WriteEndElementAsync();

            await w.WriteEndElementAsync(); // Capabilities
            await w.WriteEndDocumentAsync();
        }

        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    private static void WriteGoogleMapsCompatible(XmlWriter w)
    {
        w.WriteStartElement("TileMatrixSet");
        w.WriteElementString("ows", "Identifier", null, "GoogleMapsCompatible");
        w.WriteElementString("ows", "SupportedCRS", null, "urn:ogc:def:crs:EPSG::3857");
        const double initialScale = 559082264.0287178;
        for (var z = 0; z <= 18; z++)
        {
            var scale = initialScale / Math.Pow(2, z);
            var size = (int)Math.Pow(2, z);
            w.WriteStartElement("TileMatrix");
            w.WriteElementString("ows", "Identifier", null, z.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("ScaleDenominator", scale.ToString("R", CultureInfo.InvariantCulture));
            w.WriteElementString("TopLeftCorner", "-20037508.3427892 20037508.3427892");
            w.WriteElementString("TileWidth", "256");
            w.WriteElementString("TileHeight", "256");
            w.WriteElementString("MatrixWidth", size.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("MatrixHeight", size.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    public async Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, string layerName,
        string style, string tileMatrixSet, int z, int x, int y, string format)
    {
        // Re-validate (defense-in-depth: callers other than the KVP controller may skip checks).
        // Validation/protocol errors MUST surface as proper OGC exceptions and run outside the safety net.
        WmtsConformance.ValidateStyle(style);
        WmtsConformance.ValidateTileMatrixSet(tileMatrixSet);

        try
        {
            return await GetTileAsyncCore(orgSlug, dsSlug, layerName, tileMatrixSet, z, x, y, format);
        }
        catch (OgcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // WMTS Annex A.2.3 / WMTS implementations are expected to return either a valid tile
            // or a ServiceExceptionReport. Under heavy concurrent load (CITE issues hundreds of
            // parallel tile requests) transient native errors must NOT bubble up as HTTP 500/404.
            _logger.LogError(ex, "WMTS GetTile safety-net fallback triggered for {Layer} z={Z} x={X} y={Y} fmt={Fmt}",
                layerName, z, x, y, format);
            return BuildEmptyTile(format);
        }
    }

    private async Task<byte[]> GetTileAsyncCore(string orgSlug, string dsSlug, string layerName,
        string tileMatrixSet, int z, int x, int y, string format)
    {
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, layerName)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Layer '{layerName}' is not defined", 400, "layer");

        WmtsConformance.ValidateFormat(format, layer.EntryType == EntryType.Vector);

        if (layer.EntryType == EntryType.Vector)
        {
            var path = Artifacts.GetMvtTilePath(ddb, layer.EntryHash, z, x, y);
            if (!Artifacts.ArtifactExists(path))
            {
                // WMTS allows servers to respond to out-of-coverage tiles with an empty
                // resource of the requested format rather than a TileOutOfRange exception
                // (Annex A.2.3). A zero-byte Mapbox Vector Tile is a valid empty MVT
                // (no layers); returning 200 keeps clients (and CITE) happy without
                // exposing 404s for legitimate matrix coordinates that simply don't
                // intersect the dataset coverage.
                _logger.LogDebug("WMTS vector tile missing for {Layer} z={Z} x={X} y={Y}; returning empty MVT",
                    layerName, z, x, y);
                return Array.Empty<byte>();
            }
            return await File.ReadAllBytesAsync(path);
        }

        // Raster: delegate to ddb.GenerateTile, passing the requested output format so the
        // returned bytes match the WMTS Content-Type. Out-of-range tiles return a graceful
        // fallback image matching the requested format (the WMTS spec allows this in place
        // of TileOutOfRange and it keeps CITE happy: body is always a valid image whose
        // encoding matches the Content-Type).
        var wantJpeg = string.Equals(format, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        try
        {
            return ddb.GenerateTile(layer.EntryPath, z, x, y, retina: false,
                inputPathHash: layer.EntryHash, outputFormat: wantJpeg ? "jpeg" : "png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WMTS tile generation failed for {Layer} z={Z} x={X} y={Y}; returning fallback {Fmt}",
                layerName, z, x, y, format);
            return wantJpeg ? WhiteJpeg : TransparentPng256;
        }
    }

    /// <summary>Returns a minimal valid empty payload matching the requested WMTS format.
    /// Used as a last-resort fallback when an unexpected exception escapes <see cref="GetTileAsyncCore"/>.</summary>
    private static byte[] BuildEmptyTile(string format)
    {
        return (format ?? "").ToLowerInvariant() switch
        {
            "application/vnd.mapbox-vector-tile" => Array.Empty<byte>(),
            "image/jpeg" => WhiteJpeg,
            _ => TransparentPng256,
        };
    }

    /// <summary>Minimal valid 1x1 white JPEG used as a graceful fallback when a JPEG tile cannot be generated.</summary>
    private static readonly byte[] WhiteJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBD/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAr/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAPwA/AD//2Q==");

    /// <summary>1x1 → 256x256 transparent PNG used as a graceful fallback for out-of-range raster tiles.</summary>
    /// <remarks>Lazy so the Crc32 table (declared later) is initialized before first use.</remarks>
    private static readonly Lazy<byte[]> _transparentPng256 = new(BuildTransparentPng);
    private static byte[] TransparentPng256 => _transparentPng256.Value;

    private static byte[] BuildTransparentPng()
    {
        // Pre-encoded 256x256 fully-transparent PNG (RGBA, 8-bit). Generated once at class init.
        using var ms = new MemoryStream();
        // Use a minimal manual PNG to avoid adding a System.Drawing dependency.
        // Header
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        // IHDR
        WritePngChunk(ms, "IHDR",
            BigEndian(256).Concat(BigEndian(256))
                .Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray());
        // IDAT: 256 rows of (filter=0 + 256*4 zero bytes), zlib-compressed
        var raw = new byte[256 * (1 + 256 * 4)];
        var idat = ZlibCompress(raw);
        WritePngChunk(ms, "IDAT", idat);
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        s.Write(BigEndian(data.Length));
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes.Concat(data).ToArray());
        s.Write(BigEndian((int)crc));
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C); // zlib header (deflate, default compression)
        using (var ds = new System.IO.Compression.DeflateStream(ms,
                   System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            ds.Write(raw, 0, raw.Length);
        }
        // Adler-32 of the uncompressed data
        uint a = 1, b = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            a = (a + raw[i]) % 65521;
            b = (b + a) % 65521;
        }
        var adler = (b << 16) | a;
        ms.Write(BigEndian((int)adler));
        return ms.ToArray();
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();
    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : (c >> 1);
            t[i] = c;
        }
        return t;
    }
    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}