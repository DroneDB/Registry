using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Registry.Ports.Json;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Enumerates entries via <see cref="IDdbManager"/> and projects them to OGC layer descriptors.
/// Cached 5 minutes under a generation-scoped key (<see cref="IOgcCacheKeys"/>).
/// </summary>
public class OgcLayerCatalog : IOgcLayerCatalog
{
    private readonly IDdbManager _ddbManager;
    private readonly IDatasetResolver _datasets;
    private readonly IRecordCache _records;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly IOgcCacheKeys _keys;
    private readonly ILogger<OgcLayerCatalog> _logger;
    private static readonly RecordCacheOptions LayersCache = RecordCacheOptions.For(TimeSpan.FromMinutes(5));

    public OgcLayerCatalog(IDdbManager ddbManager, IDatasetResolver datasets, IRecordCache records,
        IBuildArtifactResolver artifacts, IDdbWrapper ddbWrapper,
        IOgcCacheKeys keys,
        ILogger<OgcLayerCatalog> logger)
    {
        _ddbManager = ddbManager;
        _datasets = datasets;
        _records = records;
        _artifacts = artifacts;
        _ddbWrapper = ddbWrapper;
        _keys = keys;
        _logger = logger;
    }

    public async Task InvalidateAsync(string orgSlug, string dsSlug)
    {
        if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug)) return;
        try
        {
            // Layer lists and every capabilities document use CacheKeyAsync: one new generation
            await _keys.InvalidateAsync(orgSlug, dsSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate OGC caches for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }

    public Task<string> CacheKeyAsync(string orgSlug, string dsSlug, string kind, string? detail = null,
        CancellationToken ct = default) =>
        _keys.ForAsync(orgSlug, dsSlug, kind, detail, ct);

    public async Task<IReadOnlyList<OgcLayerDto>> GetLayersAsync(string orgSlug, string dsSlug,
        string? folderPath = null, bool includeLocal = false, CancellationToken ct = default)
    {
        var all = await GetAllLayersAsync(orgSlug, dsSlug, folderPath, ct);
        return includeLocal ? all : all.Where(l => !l.IsLocal).ToList();
    }

    private async Task<List<OgcLayerDto>> GetAllLayersAsync(string orgSlug, string dsSlug, string? folderPath,
        CancellationToken ct)
    {
        var key = await _keys.ForAsync(orgSlug, dsSlug, "layers", folderPath, ct);
        return await _records.GetOrCreateRecordAsync(key, token => ListLayersAsync(orgSlug, dsSlug, folderPath, token),
            LayersCache, ct);
    }

    private async Task<List<OgcLayerDto>> ListLayersAsync(string orgSlug, string dsSlug, string folderPath,
        CancellationToken ct)
    {
        // Callers are the OGC managers, after the request was authorized (OgcAuthorizationFilter)
        var ds = await _datasets.GetDatasetAsync(orgSlug, dsSlug, ct: ct);
        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        var entries = ddb.Find(EntryQuery.OfTypes(folderPath, EntryType.GeoRaster, EntryType.Vector));
        var result = new List<OgcLayerDto>();

        foreach (var e in entries)
        {

            var entryBbox = ExtractBboxFromPolygon(e.PolygonGeometry);

            if (e.Type == EntryType.Vector)
            {
                var expanded = TryExpandVectorLayers(ddb, e, entryBbox);
                if (expanded != null && expanded.Count > 0)
                {
                    result.AddRange(expanded);
                    continue;
                }

                // Fallback: single layer using entry-level metadata. No GPKG sidecar was found,
                // so the layer is advertised but flagged so OGC managers can hide it from caps
                // and emit InvalidParameterValue when referenced.
                result.Add(new OgcLayerDto
                {
                    Name = e.Path,
                    Title = e.Path,
                    EntryType = e.Type,
                    EntryPath = e.Path,
                    EntryHash = e.Hash ?? string.Empty,
                    BboxWgs84 = entryBbox,
                    GeometryType = ExtractVectorGeometryType(e),
                    HasBuiltArtifact = false,
                    IsLocal = IsLocalEntry(e)
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
                    BboxWgs84 = entryBbox,
                    BandCount = ExtractBandCount(e)
                });
            }
        }

        return result;
    }

    // Index-time flag set by DroneDB for drawings without a coordinate system.
    private static bool IsLocalEntry(Entry e) =>
        e.Properties != null && e.Properties.TryGetValue("georeferenced", out var g) && g.ValueKind == JsonValueKind.False;

    /// <summary>
    /// Expand a vector entry into one <see cref="OgcLayerDto"/> per inner GPKG layer
    /// by invoking <see cref="IDdbWrapper.DescribeVector"/> on the built sidecar.
    /// Returns null when the GPKG is missing (entry not built yet) or describe fails.
    /// </summary>
    private List<OgcLayerDto>? TryExpandVectorLayers(IDDB ddb, Entry e, double[]? entryBbox)
    {
        if (string.IsNullOrEmpty(e.Hash)) return null;
        var gpkgPath = _artifacts.FindArtifactFile(ddb, e.Hash, ArtifactRoles.VectorGpkg, entryPath: e.Path);
        if (gpkgPath == null) return null;

        string json;
        try
        {
            json = _ddbWrapper.DescribeVector(gpkgPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DescribeVector failed for {Path} ({Gpkg}); falling back to entry-level layer",
                e.Path, gpkgPath);
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DescribeVector returned invalid JSON for {Path}", e.Path);
            return null;
        }

        using var _ = document;
        var layers = document.RootElement.Member("layers") is { ValueKind: JsonValueKind.Array } array ? array : (JsonElement?)null;
        if (layers is not { } layerArray || layerArray.GetArrayLength() == 0) return null;

        var list = new List<OgcLayerDto>(layerArray.GetArrayLength());
        foreach (var l in layerArray.Items())
        {
            var name = l.Member("name").Text();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var geomType = l.Member("geometryType").Text();

            // A GPKG layer without an SRS (DescribeVector reports "srs": "") is a local CAD
            // drawing: its extent is in drawing units, not WGS84.
            if (l.Member("srs") is { ValueKind: JsonValueKind.String } srs && string.IsNullOrEmpty(srs.GetString()))
            {
                list.Add(new OgcLayerDto
                {
                    Name = $"{e.Path}:{name}",
                    Title = $"{e.Path}:{name}",
                    EntryType = EntryType.Vector,
                    EntryPath = e.Path,
                    EntryHash = e.Hash ?? string.Empty,
                    InnerLayerName = name,
                    BboxWgs84 = null,
                    SupportedCrs = [],
                    GeometryType = geomType,
                    IsLocal = true
                });
                continue;
            }

            var extent = l.Member("extent") is { ValueKind: JsonValueKind.Array } e4 ? e4 : (JsonElement?)null;
            var bbox = entryBbox;
            if (extent is { } extentArray && extentArray.GetArrayLength() >= 4)
            {
                try
                {
                    bbox =
                    [
                        extentArray[0].ToDouble(),
                        extentArray[1].ToDouble(),
                        extentArray[2].ToDouble(),
                        extentArray[3].ToDouble()
                    ];
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "OgcLayerCatalog: failed to parse layer extent for {Path}; using entry bbox", e.Path);
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
        string? folderPath = null, bool includeLocal = false, CancellationToken ct = default)
    {
        var layers = await GetLayersAsync(orgSlug, dsSlug, folderPath, includeLocal, ct);
        // Strip optional namespace prefix (e.g. "ddb:foo" -> "foo")
        var localName = layerName;
        var colon = layerName.IndexOf(':');
        if (colon >= 0 && colon < layerName.Length - 1) localName = layerName[(colon + 1)..];

        var matches = layers.Where(l => string.Equals(l.Name, layerName, StringComparison.Ordinal)
                                        || string.Equals(l.Name, localName, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            // Fallback: match by sanitized name (NCName-safe).
            matches =
            [
                .. layers.Where(l => string.Equals(OgcNames.ToNcName(l.Name), localName, StringComparison.Ordinal)
                                     || string.Equals(OgcNames.ToNcName(l.Name), OgcNames.ToNcName(layerName),
                                         StringComparison.Ordinal))
            ];
        }

        if (matches.Count == 0)
        {
            // Fallback: when the client asks for the bare entry path of a multi-layer vector,
            // resolve to the first inner layer so legacy single-layer URLs keep working.
            matches =
            [
                .. layers.Where(l => string.Equals(l.EntryPath, layerName, StringComparison.Ordinal)
                                     || string.Equals(l.EntryPath, localName, StringComparison.Ordinal))
            ];
        }

        if (matches.Count == 0) return null;
        // When multiple entries share a name (e.g. raw vs. built artifact), prefer the one with a bbox.
        return matches.FirstOrDefault(l => l.BboxWgs84 != null) ?? matches[0];
    }


    private static double[]? ExtractBboxFromPolygon(JsonElement? polygonGeom)
    {
        if (polygonGeom is not { } token) return null;
        try
        {
            // Support GeoJSON Feature: unwrap to geometry
            var typeStr = token.Member("type").Text();
            if (string.Equals(typeStr, "Feature", StringComparison.OrdinalIgnoreCase))
                token = token.Member("geometry") ?? token;

            if (token.Member("coordinates") is not { ValueKind: JsonValueKind.Array } coords) return null;
            // GeoJSON Polygon coordinates: [ [ [lon,lat], [lon,lat], ... ] ]
            if (coords.GetArrayLength() == 0 || coords[0].ValueKind != JsonValueKind.Array || coords[0].GetArrayLength() == 0)
                return null;
            double minLon = double.MaxValue,
                minLat = double.MaxValue,
                maxLon = double.MinValue,
                maxLat = double.MinValue;
            foreach (var pt in coords[0].Items())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var lon = pt[0].ToDouble();
                var lat = pt[1].ToDouble();
                if (lon < minLon) minLon = lon;
                if (lat < minLat) minLat = lat;
                if (lon > maxLon) maxLon = lon;
                if (lat > maxLat) maxLat = lat;
            }

            if (minLon == double.MaxValue) return null;
            return [minLon, minLat, maxLon, maxLat];
        }
        catch (Exception ex)
        {
            // Static method: no logger available. Best-effort fallback.
            System.Diagnostics.Debug.WriteLine($"ExtractBboxFromPolygon failed: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractVectorGeometryType(Entry e)
    {
        if (e.Properties == null) return null;
        return e.Properties.TryGetValue("vector", out var v) && v.ValueKind == JsonValueKind.Object
            ? v.Member("geometryType").Text()
            : null;
    }

    /// <summary>
    /// The number of bands of a GeoRaster entry, read from its indexed <c>bands</c> property (0 when it is missing or
    /// unparseable). WCS describes a coverage with it when the raster probe fails. Which spectral indices a raster can
    /// render is not decided from the bands: libddb lists them (<see cref="IRasterIndexCatalog"/>).
    /// </summary>
    internal static int ExtractBandCount(Entry e)
    {
        if (e.Properties == null || !e.Properties.TryGetValue("bands", out var raw))
            return 0;

        switch (raw.ValueKind)
        {
            case JsonValueKind.Array:
                return raw.GetArrayLength();
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(raw.GetString()):
                try
                {
                    // The bands as text: a JSON array the index stored as a string
                    using var document = JsonDocument.Parse(raw.GetString()!);
                    return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
                }
                catch (JsonException)
                {
                    return 0;
                }
            default:
                return 0;
        }
    }
}
