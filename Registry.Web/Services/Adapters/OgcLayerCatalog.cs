using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Enumerates entries via <see cref="IDdbManager"/> and projects them to OGC layer descriptors.
/// Cached 5 minutes in Redis under "ogc-layers-{org}-{ds}[-{folder}]".
/// </summary>
public class OgcLayerCatalog : IOgcLayerCatalog
{
    private readonly IDdbManager _ddbManager;
    private readonly IUtils _utils;
    private readonly IDistributedCache _cache;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly ICacheKeyScanner _keyScanner;
    private readonly ILogger<OgcLayerCatalog> _logger;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public OgcLayerCatalog(IDdbManager ddbManager, IUtils utils, IDistributedCache cache,
        IBuildArtifactResolver artifacts, IDdbWrapper ddbWrapper,
        ICacheKeyScanner keyScanner,
        ILogger<OgcLayerCatalog> logger)
    {
        _ddbManager = ddbManager;
        _utils = utils;
        _cache = cache;
        _artifacts = artifacts;
        _ddbWrapper = ddbWrapper;
        _keyScanner = keyScanner;
        _logger = logger;
    }

    public async Task InvalidateAsync(string orgSlug, string dsSlug)
    {
        if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug)) return;
        try
        {
            // Capabilities keys: ogc-caps-{service}-v2-{version}-{org}-{ds}-{folder}
            await _keyScanner.RemoveByPatternAsync($"ogc-caps-*-{orgSlug}-{dsSlug}-*");
            // Layer enumeration keys: ogc-layers-{org}-{ds}-{folder}
            await _keyScanner.RemoveByPatternAsync($"ogc-layers-{orgSlug}-{dsSlug}-*");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate OGC caches for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }

    public async Task<IReadOnlyList<OgcLayerDto>> GetLayersAsync(string orgSlug, string dsSlug, string? folderPath = null)
    {
        var key = $"ogc-layers-{orgSlug}-{dsSlug}-{folderPath ?? string.Empty}";
        var cached = await _cache.GetRecordAsync<List<OgcLayerDto>>(key);
        if (cached != null) return cached;

        var ds = _utils.GetDataset(orgSlug, dsSlug);
        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        var entries = ddb.Search(folderPath ?? string.Empty, recursive: true) ?? Enumerable.Empty<Entry>();
        var result = new List<OgcLayerDto>();

        foreach (var e in entries)
        {
            if (e == null) continue;
            if (e.Type != EntryType.GeoRaster && e.Type != EntryType.Vector) continue;

            var entryBbox = ExtractBboxFromPolygon(e.PolygonGeometry);

            if (e.Type == EntryType.Vector)
            {
                var expanded = TryExpandVectorLayers(ddb, e, entryBbox);
                if (expanded != null && expanded.Count > 0)
                {
                    result.AddRange(expanded);
                    continue;
                }
                // Fallback: single layer using entry-level metadata.
                result.Add(new OgcLayerDto
                {
                    Name = e.Path,
                    Title = e.Path,
                    EntryType = e.Type,
                    EntryPath = e.Path,
                    EntryHash = e.Hash ?? string.Empty,
                    BboxWgs84 = entryBbox,
                    GeometryType = ExtractVectorGeometryType(e)
                });
            }
            else
            {
                result.Add(new OgcLayerDto
                {
                    Name = e.Path,
                    Title = e.Path,
                    EntryType = e.Type,
                    EntryPath = e.Path,
                    EntryHash = e.Hash ?? string.Empty,
                    BboxWgs84 = entryBbox
                });
            }
        }

        await _cache.SetRecordAsync(key, result, Ttl);
        return result;
    }

    /// <summary>
    /// Expand a vector entry into one <see cref="OgcLayerDto"/> per inner GPKG layer
    /// by invoking <see cref="IDdbWrapper.DescribeVector"/> on the built sidecar.
    /// Returns null when the GPKG is missing (entry not built yet) or describe fails.
    /// </summary>
    private List<OgcLayerDto>? TryExpandVectorLayers(IDDB ddb, Entry e, double[]? entryBbox)
    {
        if (string.IsNullOrEmpty(e.Hash)) return null;
        var gpkgPath = _artifacts.GetVectorQueryPath(ddb, e.Hash);
        if (!_artifacts.ArtifactExists(gpkgPath)) return null;

        string json;
        try
        {
            json = _ddbWrapper.DescribeVector(gpkgPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DescribeVector failed for {Path} ({Gpkg}); falling back to entry-level layer", e.Path, gpkgPath);
            return null;
        }

        JToken root;
        try { root = JToken.Parse(json); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DescribeVector returned invalid JSON for {Path}", e.Path);
            return null;
        }

        var layers = root["layers"] as JArray;
        if (layers == null || layers.Count == 0) return null;

        var list = new List<OgcLayerDto>(layers.Count);
        foreach (var l in layers)
        {
            var name = l["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var geomType = l["geometryType"]?.Value<string>();
            var extent = l["extent"] as JArray;
            double[]? bbox = entryBbox;
            if (extent != null && extent.Count >= 4)
            {
                try
                {
                    bbox =
                    [
                        extent[0].Value<double>(),
                        extent[1].Value<double>(),
                        extent[2].Value<double>(),
                        extent[3].Value<double>()
                    ];
                }
                catch
                {
                    bbox = entryBbox;
                }
            }

            list.Add(new OgcLayerDto
            {
                Name = $"{e.Path}:{name}",
                Title = $"{e.Path}:{name}",
                EntryType = EntryType.Vector,
                EntryPath = e.Path,
                EntryHash = e.Hash ?? string.Empty,
                InnerLayerName = name,
                BboxWgs84 = bbox,
                GeometryType = geomType
            });
        }

        return list.Count > 0 ? list : null;
    }

    public async Task<OgcLayerDto?> ResolveAsync(string orgSlug, string dsSlug, string layerName,
        string? folderPath = null)
    {
        var layers = await GetLayersAsync(orgSlug, dsSlug, folderPath);
        // Strip optional namespace prefix (e.g. "ddb:foo" -> "foo")
        var localName = layerName;
        var colon = layerName.IndexOf(':');
        if (colon >= 0 && colon < layerName.Length - 1) localName = layerName.Substring(colon + 1);

        var matches = layers.Where(l => string.Equals(l.Name, layerName, StringComparison.Ordinal)
                                        || string.Equals(l.Name, localName, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            // Fallback: match by sanitized name (NCName-safe).
            matches = layers.Where(l => string.Equals(SanitizeName(l.Name), localName, StringComparison.Ordinal)
                                        || string.Equals(SanitizeName(l.Name), SanitizeName(layerName), StringComparison.Ordinal)).ToList();
        }
        if (matches.Count == 0)
        {
            // Fallback: when the client asks for the bare entry path of a multi-layer vector,
            // resolve to the first inner layer so legacy single-layer URLs keep working.
            matches = layers.Where(l => string.Equals(l.EntryPath, layerName, StringComparison.Ordinal)
                                        || string.Equals(l.EntryPath, localName, StringComparison.Ordinal)).ToList();
        }
        if (matches.Count == 0) return null;
        // When multiple entries share a name (e.g. raw vs. built artifact), prefer the one with a bbox.
        return matches.FirstOrDefault(l => l.BboxWgs84 != null) ?? matches[0];
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_unnamed";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
            else sb.Append('_');
        }
        var s = sb.ToString();
        if (!(char.IsLetter(s[0]) || s[0] == '_')) s = "_" + s;
        return s;
    }

    private static double[]? ExtractBboxFromPolygon(object? polygonGeom)
    {
        if (polygonGeom == null) return null;
        try
        {
            var token = polygonGeom as JToken ?? JToken.FromObject(polygonGeom);
            // Support GeoJSON Feature: unwrap to geometry
            var typeStr = token["type"]?.Value<string>();
            if (string.Equals(typeStr, "Feature", StringComparison.OrdinalIgnoreCase))
            {
                token = token["geometry"] ?? token;
            }
            var coords = token["coordinates"];
            if (coords == null || coords.Type != JTokenType.Array) return null;
            // GeoJSON Polygon coordinates: [ [ [lon,lat], [lon,lat], ... ] ]
            var ring = coords.First as JArray;
            if (ring == null || ring.Count == 0) return null;
            double minLon = double.MaxValue, minLat = double.MaxValue,
                   maxLon = double.MinValue, maxLat = double.MinValue;
            foreach (var pt in ring)
            {
                if (pt is not JArray pa || pa.Count < 2) continue;
                var lon = pa[0].Value<double>();
                var lat = pa[1].Value<double>();
                if (lon < minLon) minLon = lon;
                if (lat < minLat) minLat = lat;
                if (lon > maxLon) maxLon = lon;
                if (lat > maxLat) maxLat = lat;
            }
            if (minLon == double.MaxValue) return null;
            return [minLon, minLat, maxLon, maxLat];
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractVectorGeometryType(Entry e)
    {
        if (e.Properties == null) return null;
        if (e.Properties.TryGetValue("vector", out var v) && v is JToken token)
        {
            return token["geometryType"]?.Value<string>();
        }
        return null;
    }
}
