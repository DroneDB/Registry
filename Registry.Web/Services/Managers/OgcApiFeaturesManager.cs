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
using Newtonsoft.Json;
using Formatting = Newtonsoft.Json.Formatting;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>
/// OGC API – Features manager (JSON-based REST).
/// </summary>
public class OgcApiFeaturesManager : OgcManagerBase, IOgcApiFeaturesManager
{
    private readonly ILogger<OgcApiFeaturesManager> _logger;

    public OgcApiFeaturesManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx, ILogger<OgcApiFeaturesManager> logger)
        : base(u, a, d, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    public Task<OgcConformanceDto> GetConformanceAsync() => Task.FromResult(new OgcConformanceDto());

    public async Task<OgcApiLandingDto> GetLandingAsync(string orgSlug, string dsSlug, string baseUrl)
    {
        await ResolveAsync(orgSlug, dsSlug);
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

    public async Task<OgcApiCollectionsDto> GetCollectionsAsync(string orgSlug, string dsSlug, string baseUrl)
    {
        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
        var b = baseUrl.TrimEnd('/');
        var dto = new OgcApiCollectionsDto
        {
            Links = [new() { Href = $"{b}/collections", Rel = "self", Type = "application/json" }]
        };
        foreach (var l in layers)
        {
            dto.Collections.Add(BuildCollection(l, b));
        }
        return dto;
    }

    public async Task<OgcApiCollectionDto?> GetCollectionAsync(string orgSlug, string dsSlug,
        string collectionId, string baseUrl)
    {
        var name = Uri.UnescapeDataString(collectionId);
        await ResolveAsync(orgSlug, dsSlug);
        // Local layers (no CRS) resolve here and in items for the Hub's local viewer; they are
        // never listed in /collections.
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, includeLocal: true);
        return layer == null ? null : BuildCollection(layer, baseUrl.TrimEnd('/'));
    }

    public async Task<string> GetItemsAsync(string orgSlug, string dsSlug, string collectionId,
        double[]? bbox, int limit, int offset)
    {
        if (limit <= 0) limit = 10;
        limit = Math.Clamp(limit, 1, 1000);
        if (offset < 0) offset = 0;

        var (gpkg, layer) = await ResolveFeatureCollectionAsync(orgSlug, dsSlug, collectionId);
        if (layer.IsLocal && bbox != null)
            throw new OgcException("InvalidParameterValue",
                "This collection has no coordinate system (local drawing coordinates); bbox is not supported",
                400, "bbox");

        return DdbWrapper.QueryVector(gpkg, new VectorQuery(layer.InnerLayerName, bbox,
            bbox != null ? "EPSG:4326" : null, MaxFeatures: limit, StartIndex: offset));
    }

    public async Task<string> GetItemAsync(string orgSlug, string dsSlug, string collectionId, string featureId)
    {
        // Feature ids are the GeoPackage fids (GeoJSON "id"): one primary-key read
        if (!long.TryParse(featureId, NumberStyles.None, CultureInfo.InvariantCulture, out var fid))
            return "{}";

        var (gpkg, layer) = await ResolveFeatureCollectionAsync(orgSlug, dsSlug, collectionId);
        var items = DdbWrapper.QueryVector(gpkg, new VectorQuery(layer.InnerLayerName, Fids: [fid], MaxFeatures: 1));
        try
        {
            return (JObject.Parse(items)["features"] as JArray)?.FirstOrDefault()?.ToString(Formatting.None) ?? "{}";
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
        string collectionId)
    {
        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, includeLocal: true)
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

