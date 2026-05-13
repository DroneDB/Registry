using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
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
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, ILogger<WmtsManager> logger)
        : base(u, a, d, ar, w, c, cache)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null)
    {
        var key = $"ogc-caps-wmts-1.0.0-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);

        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("Capabilities", "http://www.opengis.net/wmts/1.0");
            w.WriteAttributeString("xmlns", "ows", null, "http://www.opengis.net/ows/1.1");
            w.WriteAttributeString("xmlns", "xlink", null, "http://www.w3.org/1999/xlink");
            w.WriteAttributeString("version", "1.0.0");

            // ServiceIdentification
            w.WriteStartElement("ows", "ServiceIdentification", null);
            w.WriteElementString("ows", "Title", null, $"DroneDB WMTS — {orgSlug}/{dsSlug}");
            w.WriteElementString("ows", "ServiceType", null, "OGC WMTS");
            w.WriteElementString("ows", "ServiceTypeVersion", null, "1.0.0");
            w.WriteEndElement();

            // Contents
            w.WriteStartElement("Contents");
            foreach (var layer in layers)
            {
                w.WriteStartElement("Layer");
                w.WriteElementString("ows", "Title", null, layer.Title);
                w.WriteElementString("ows", "Identifier", null, layer.Name);
                if (layer.BboxWgs84 != null)
                {
                    w.WriteStartElement("ows", "WGS84BoundingBox", null);
                    w.WriteElementString("ows", "LowerCorner", null,
                        FormattableString.Invariant($"{layer.BboxWgs84[0]} {layer.BboxWgs84[1]}"));
                    w.WriteElementString("ows", "UpperCorner", null,
                        FormattableString.Invariant($"{layer.BboxWgs84[2]} {layer.BboxWgs84[3]}"));
                    w.WriteEndElement();
                }
                w.WriteStartElement("Style");
                w.WriteAttributeString("isDefault", "true");
                w.WriteElementString("ows", "Identifier", null, "default");
                w.WriteEndElement();
                w.WriteElementString("Format",
                    layer.EntryType == EntryType.Vector
                        ? "application/vnd.mapbox-vector-tile"
                        : "image/png");
                w.WriteStartElement("TileMatrixSetLink");
                w.WriteElementString("TileMatrixSet", "GoogleMapsCompatible");
                w.WriteEndElement();
                w.WriteEndElement(); // Layer
            }

            WriteGoogleMapsCompatible(w);
            w.WriteEndElement(); // Contents

            w.WriteEndElement(); // Capabilities
            w.WriteEndDocument();
        }

        var xml = sb.ToString();
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    private static void WriteGoogleMapsCompatible(XmlWriter w)
    {
        w.WriteStartElement("TileMatrixSet");
        w.WriteElementString("ows", "Identifier", null, "GoogleMapsCompatible");
        w.WriteElementString("ows", "SupportedCRS", null, "urn:ogc:def:crs:EPSG::3857");
        var initialScale = 559082264.0287178;
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
        string tileMatrixSet, int z, int x, int y, string format)
    {
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, layerName)
                    ?? throw new OgcException("LayerNotDefined", $"Layer '{layerName}' not found", 404, "Layer");

        if (layer.EntryType == EntryType.Vector)
        {
            var path = Artifacts.GetMvtTilePath(ddb, layer.EntryHash, z, x, y);
            if (!Artifacts.ArtifactExists(path))
                throw new OgcException("TileOutOfRange", "Tile not found", 404);
            return await File.ReadAllBytesAsync(path);
        }

        // Raster: reuse existing pipeline via IDDB.GenerateTile.
        return ddb.GenerateTile(layer.EntryPath, z, x, y, retina: false, inputPathHash: layer.EntryHash);
    }
}
