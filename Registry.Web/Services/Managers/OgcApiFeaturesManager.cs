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
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>OGC API – Features manager (JSON-based REST).</summary>
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
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name);
        return layer == null ? null : BuildCollection(layer, baseUrl.TrimEnd('/'));
    }

    public async Task<string> GetItemsAsync(string orgSlug, string dsSlug, string collectionId,
        double[]? bbox, int limit, int offset)
    {
        if (limit <= 0) limit = 10;
        limit = Math.Clamp(limit, 1, 1000);
        if (offset < 0) offset = 0;

        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404, "collectionId");
        if (layer.EntryType != EntryType.Vector)
            throw new OgcException("OperationNotSupported",
                "OGC API – Features supports only Vector layers", 400);

        var gpkg = ResolveVectorArtifact(ddb, layer);
        return DdbWrapper.QueryVector(gpkg, layer.InnerLayerName, bbox, bbox != null ? "EPSG:4326" : null,
            limit, offset, "application/json");
    }

    public async Task<string> GetItemAsync(string orgSlug, string dsSlug, string collectionId, string featureId)
    {
        // Best-effort: pull a single page of features and filter client-side by 'id'/'fid' property.
        var items = await GetItemsAsync(orgSlug, dsSlug, collectionId, bbox: null, limit: 1000, offset: 0);
        try
        {
            var fc = JObject.Parse(items);
            var features = fc["features"] as JArray;
            if (features == null) return "{}";
            var match = features.OfType<JObject>().FirstOrDefault(f =>
                f["id"]?.ToString() == featureId
                || (f["properties"] is JObject p && p["fid"]?.ToString() == featureId));
            return match?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "OGC API Features: failed to parse items for {Org}/{Ds}/{Collection}/{FeatureId}",
                orgSlug, dsSlug, collectionId, featureId);
            return "{}";
        }
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

