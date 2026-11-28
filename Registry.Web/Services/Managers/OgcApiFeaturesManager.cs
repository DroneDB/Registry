using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Registry.Ports.Json;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>
/// OGC API – Features manager (JSON-based REST).
/// </summary>
public class OgcApiFeaturesManager : OgcManagerBase
{
    private readonly ILogger<OgcApiFeaturesManager> _logger;

    public OgcApiFeaturesManager(OgcManagerDependencies dependencies, ILogger<OgcApiFeaturesManager> logger)
        : base(dependencies)
    {
        _logger = logger;
    }

    public Task<OgcConformanceDto> GetConformanceAsync() => Task.FromResult(new OgcConformanceDto());

    public async Task<OgcApiLandingDto> GetLandingAsync(string orgSlug, string dsSlug, string baseUrl,
        CancellationToken ct = default)
    {
        await ResolveAsync(orgSlug, dsSlug, ct);
        var b = baseUrl.TrimEnd('/');
        return new OgcApiLandingDto
        {
            Title = $"OGC API for {orgSlug}/{dsSlug}",
            Links =
            [
                new() { Href = $"{b}/", Rel = "self", Type = "application/json" },
                new() { Href = $"{b}/conformance", Rel = "conformance", Type = "application/json" },
                new() { Href = $"{b}/collections", Rel = "data", Type = "application/json" }
            ]
        };
    }

    public async Task<OgcApiCollectionsDto> GetCollectionsAsync(string orgSlug, string dsSlug, string baseUrl,
        CancellationToken ct = default)
    {
        await ResolveAsync(orgSlug, dsSlug, ct);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, ct: ct);
        var b = baseUrl.TrimEnd('/');
        var dto = new OgcApiCollectionsDto
        {
            Links = [new() { Href = $"{b}/collections", Rel = "self", Type = "application/json" }]
        };
        // A raster without a COG is left out, as from the capabilities of the other OGC services
        foreach (var l in layers.Where(l => !IsUnbuiltRaster(l)))
        {
            dto.Collections.Add(BuildCollection(l, b));
        }
        return dto;
    }

    public async Task<OgcApiCollectionDto?> GetCollectionAsync(string orgSlug, string dsSlug,
        string collectionId, string baseUrl, CancellationToken ct = default)
    {
        var name = Uri.UnescapeDataString(collectionId);
        await ResolveAsync(orgSlug, dsSlug, ct);
        // Local layers (no CRS) resolve here and in items for the Hub's local viewer; they are
        // never listed in /collections.
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, includeLocal: true, ct: ct);
        // A raster without a COG is not found, as it is not in /collections
        return layer == null || IsUnbuiltRaster(layer) ? null : BuildCollection(layer, baseUrl.TrimEnd('/'));
    }

    public async Task<string> GetItemsAsync(string orgSlug, string dsSlug, string collectionId,
        double[]? bbox, int limit, int offset, CancellationToken ct = default)
    {
        if (limit <= 0) limit = 10;
        limit = Math.Clamp(limit, 1, 1000);
        if (offset < 0) offset = 0;

        var (gpkg, layer) = await ResolveFeatureCollectionAsync(orgSlug, dsSlug, collectionId, ct);
        if (layer.IsLocal && bbox != null)
            throw new OgcException("InvalidParameterValue",
                "This collection has no coordinate system (local drawing coordinates); bbox is not supported",
                400, "bbox");

        return DdbWrapper.QueryVector(gpkg, new VectorQuery(layer.InnerLayerName, bbox,
            bbox != null ? "EPSG:4326" : null, MaxFeatures: limit, StartIndex: offset));
    }

    public async Task<string> GetItemAsync(string orgSlug, string dsSlug, string collectionId, string featureId,
        CancellationToken ct = default)
    {
        // Feature ids are the GeoPackage fids (GeoJSON "id"): one primary-key read
        if (!long.TryParse(featureId, NumberStyles.None, CultureInfo.InvariantCulture, out var fid))
            return "{}";

        var (gpkg, layer) = await ResolveFeatureCollectionAsync(orgSlug, dsSlug, collectionId, ct);
        var items = DdbWrapper.QueryVector(gpkg, new VectorQuery(layer.InnerLayerName, Fids: [fid], MaxFeatures: 1));
        try
        {
            using var collection = JsonDocument.Parse(items);
            return collection.RootElement.Member("features") is { ValueKind: JsonValueKind.Array } features &&
                   features.GetArrayLength() > 0
                ? features[0].GetRawText()
                : "{}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "OGC API Features: failed to parse items for {Org}/{Ds}/{Collection}/{FeatureId}",
                orgSlug, dsSlug, collectionId, featureId);
            return "{}";
        }
    }

    /// <summary>The GeoPackage and layer of a vector collection (local layers included).</summary>
    private async Task<(string Gpkg, OgcLayerDto Layer)> ResolveFeatureCollectionAsync(string orgSlug, string dsSlug,
        string collectionId, CancellationToken ct)
    {
        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, includeLocal: true, ct: ct)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404, "collectionId");
        if (layer.EntryType != EntryType.Vector)
            throw new OgcException("OperationNotSupported",
                "OGC API – Features supports only Vector layers", 400);
        return (ResolveVectorArtifact(ddb, layer), layer);
    }

    private static OgcApiCollectionDto BuildCollection(OgcLayerDto layer, string baseUrl)
    {
        var id = Uri.EscapeDataString(layer.Name);
        return new OgcApiCollectionDto
        {
            Id = id,
            Title = layer.Title,
            ItemType = layer.EntryType == EntryType.Vector ? "feature" : "coverage",
            Extent = layer.BboxWgs84 != null
                ? new OgcApiExtentDto
                {
                    Spatial = new OgcApiSpatialExtentDto { Bbox = [layer.BboxWgs84] }
                }
                : null,
            Links =
            [
                new() { Href = $"{baseUrl}/collections/{id}", Rel = "self", Type = "application/json" },
                new()
                {
                    Href = $"{baseUrl}/collections/{id}/items", Rel = "items",
                    Type = "application/geo+json"
                }
            ]
        };
    }
}

