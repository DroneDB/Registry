using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

public class StacManager : IStacManager
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IUtils _utils;
    private readonly IDdbManager _ddbManager;
    private readonly IDistributedCache _cache;
    private readonly ILogger<StacManager> _logger;

    private const string CacheKey = "stac-catalog";

    // These could end up in the config
    private const string CatalogTitle = "DroneDB public datasets catalog";
    private const string CatalogId = "DroneDB Catalog";
    private const string StacVersion = "1.1.0";
    private readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

    // STAC API 1.0.0 conformance classes implemented by this server
    private static readonly string[] ConformanceClasses =
    {
        "https://api.stacspec.org/v1.0.0/core",
        "https://api.stacspec.org/v1.0.0/collections",
        "https://api.stacspec.org/v1.0.0/ogcapi-features",
        "https://api.stacspec.org/v1.0.0/item-search",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson"
    };

    private const int DefaultLimit = 10;
    private const int MaxLimit = 10000;


    public StacManager(
        IAuthManager authManager,
        RegistryContext context,
        IUtils utils,
        IDdbManager ddbManager,
        IDistributedCache cache,
        ILogger<StacManager> logger)
    {
        _authManager = authManager;
        _context = context;
        _utils = utils;
        _ddbManager = ddbManager;
        _cache = cache;
        _logger = logger;
    }


    public async Task<StacCatalogDto> GetCatalog()
    {

        var stacUrl = _utils.GenerateStacUrl();

        var links = new List<StacLinkDto>
        {
            new()
            {
                Href = stacUrl,
                Relationship = "self",
                Title = CatalogTitle
            },
            new()
            {
                Href = stacUrl,
                Relationship = "root",
                Title = CatalogTitle
            }
        };

        var datasets = _context.Datasets.Include(ds => ds.Organization).ToArray();

        foreach (var ds in datasets)
        {

            var key = MakeCacheKey(ds);

            var item = await _cache.GetRecordAsync<StacLinkDto>(key);

            if (item != null)
            {
                links.Add(item);
                await _cache.RefreshAsync(key);
                continue;
            }

            var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);
            var meta = ddb.Meta.GetSafe();
            if (meta.Visibility != Visibility.Public) continue;

            item = new StacLinkDto
            {
                Href = _utils.GenerateDatasetStacUrl(ds.Organization.Slug, ds.Slug),
                Relationship = "child",
                Title = meta.Name
            };

            await _cache.SetRecordAsync(key, item, Expiration);

            links.Add(item);
        }

        var catalog = new StacCatalogDto
        {
            Type = "Catalog",
            StacVersion = StacVersion,
            Id = CatalogId,
            Description = CatalogTitle,
            Links = links,
        };

        return catalog;

    }

    public async Task ClearCache(Dataset ds)
    {
        _logger.LogInformation("In ClearCache('{DsSlug}')", ds.Slug);

        await _cache.RemoveAsync(MakeCacheKey(ds));
    }

    public async Task<JToken> GetStacChild(string orgSlug, string dsSlug, string path = null)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In GetStacChild('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to list this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        if (path != null && !ddb.EntryExists(path))
            throw new ArgumentException("Entry does not exist");

        return ddb.GetStac($"{orgSlug}/{dsSlug}", _utils.GenerateDatasetUrl(ds),
            _utils.GetLocalHost(), path);

    }

    #region STAC API 1.0.0

    public async Task<StacCatalogDto> GetLandingPage()
    {
        var cacheKey = $"stac-landing-{await _authManager.SafeGetCurrentUserName()}";
        var cached = await _cache.GetRecordAsync<StacCatalogDto>(cacheKey);
        if (cached != null)
            return cached;

        var root = StacRoot();
        var host = _utils.GetLocalHost();

        var links = new List<StacLinkDto>
        {
            new() { Href = root, Relationship = "self", Type = "application/json", Title = CatalogTitle },
            new() { Href = root, Relationship = "root", Type = "application/json", Title = CatalogTitle },
            new() { Href = root + "/conformance", Relationship = "conformance", Type = "application/json" },
            new() { Href = root + "/collections", Relationship = "data", Type = "application/json" },
            new() { Href = root + "/search", Relationship = "search", Type = "application/geo+json", Method = "GET" },
            new() { Href = root + "/search", Relationship = "search", Type = "application/geo+json", Method = "POST" },
            new() { Href = host + "/swagger/v1/swagger.json", Relationship = "service-desc", Type = "application/json" },
            new() { Href = host + "/scalar/v1", Relationship = "service-doc", Type = "text/html" }
        };

        foreach (var item in await GetAccessibleDatasets())
        {
            links.Add(new StacLinkDto
            {
                Href = CollectionUrl(item.ds.Organization.Slug, item.ds.Slug),
                Relationship = "child",
                Type = "application/json",
                Title = item.meta.Name
            });
        }

        var result = new StacCatalogDto
        {
            Type = "Catalog",
            StacVersion = StacVersion,
            Id = CatalogId,
            Description = CatalogTitle,
            ConformsTo = ConformanceClasses,
            Links = links
        };

        await _cache.SetRecordAsync(cacheKey, result, Expiration);
        return result;
    }

    public StacConformanceDto GetConformance()
    {
        return new StacConformanceDto { ConformsTo = ConformanceClasses };
    }

    public async Task<StacCollectionsDto> GetCollections()
    {
        var cacheKey = $"stac-collections-{await _authManager.SafeGetCurrentUserName()}";
        var cached = await _cache.GetRecordAsync<StacCollectionsDto>(cacheKey);
        if (cached != null)
            return cached;

        var collections = new List<JToken>();
        foreach (var (ds, _) in await GetAccessibleDatasets())
        {
            var collection = GetCollectionInternal(ds.Organization.Slug, ds.Slug, ds.InternalRef);
            if (collection != null)
                collections.Add(collection);
        }

        var links = new List<StacLinkDto>
        {
            new() { Href = CollectionsUrl(), Relationship = "self", Type = "application/json" },
            new() { Href = StacRoot(), Relationship = "root", Type = "application/json" },
            new() { Href = StacRoot(), Relationship = "parent", Type = "application/json" }
        };

        var result = new StacCollectionsDto { Collections = collections, Links = links };
        await _cache.SetRecordAsync(cacheKey, result, Expiration);
        return result;
    }

    public async Task<JToken> GetCollection(string orgSlug, string dsSlug)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to access this dataset");

        return GetCollectionInternal(orgSlug, dsSlug, ds.InternalRef);
    }

    public async Task<JToken> GetCollectionItems(string orgSlug, string dsSlug, double[] bbox = null,
        string datetime = null, int? limit = null, int? offset = null)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to access this dataset");

        var collectionId = $"{orgSlug}/{dsSlug}";
        var lim = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var off = Math.Max(offset ?? 0, 0);

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        var bboxStr = BboxToString(bbox);

        if (ddb.GetStacItemCollection(collectionId, _utils.GenerateDatasetUrl(ds), _utils.GetLocalHost(),
                bboxStr, datetime, lim, off) is not JObject result)
            throw new InvalidOperationException("Invalid STAC item collection result");

        var features = result["features"] as JArray ?? new JArray();
        foreach (var feature in features.OfType<JObject>())
            RewriteItem(feature, orgSlug, dsSlug, collectionId);

        var numberMatched = result["numberMatched"]?.Value<long>() ?? features.Count;

        // The self link must match the requested URL exactly, so only echo back the
        // query parameters the client actually provided (STAC API conformance).
        var selfQuery = BuildItemsSelfQuery(bbox, datetime, limit, offset);
        var links = new JArray
        {
            Link("self", ItemsUrl(orgSlug, dsSlug) + selfQuery, "application/geo+json"),
            Link("root", StacRoot(), "application/json"),
            Link("collection", CollectionUrl(orgSlug, dsSlug), "application/json")
        };

        if (off + features.Count < numberMatched)
            links.Add(Link("next", ItemsUrl(orgSlug, dsSlug) + BuildItemsQuery(bbox, datetime, lim, off + lim),
                "application/geo+json"));

        result["links"] = links;
        return result;
    }

    public async Task<JToken> GetCollectionItem(string orgSlug, string dsSlug, string featureId)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to access this dataset");

        var collectionId = $"{orgSlug}/{dsSlug}";

        // An undecodable feature id simply identifies a feature that does not exist (404),
        // not a malformed request (400).
        if (!TryDecodeFeatureId(featureId, out var path))
            throw new NotFoundException("Feature does not exist");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        if (!ddb.EntryExists(path))
            throw new NotFoundException("Feature does not exist");

        if (ddb.GetStac(collectionId, _utils.GenerateDatasetUrl(ds), _utils.GetLocalHost(), path) is not JObject item)
            throw new InvalidOperationException("Invalid STAC item result");

        RewriteItem(item, orgSlug, dsSlug, collectionId);
        return item;
    }

    public async Task<JToken> Search(StacSearchRequestDto request)
    {
        request ??= new StacSearchRequestDto();

        var lim = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit);
        var bboxStr = BboxToString(request.Bbox);
        var datetime = request.Datetime;

        var datasets = (await GetAccessibleDatasets()).Select(x => x.ds).ToList();

        if (request.Collections is { Length: > 0 })
        {
            var wanted = new HashSet<string>(request.Collections);
            datasets = datasets
                .Where(d => wanted.Contains($"{d.Organization.Slug}/{d.Slug}"))
                .ToList();
        }

        if (request.Ids is { Length: > 0 })
            return SearchByIds(datasets, request.Ids, bboxStr, datetime);

        DecodeToken(request.Token, out var startDs, out var startOffset);

        var features = new JArray();
        var resumeDs = -1;
        var resumeOffset = 0;

        for (var i = startDs; i < datasets.Count; i++)
        {
            var ds = datasets[i];
            var collectionId = $"{ds.Organization.Slug}/{ds.Slug}";
            var off = i == startDs ? startOffset : 0;
            var need = lim - features.Count;

            var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

            if (ddb.GetStacItemCollection(collectionId, _utils.GenerateDatasetUrl(ds), _utils.GetLocalHost(),
                    bboxStr, datetime, need, off) is not JObject fc)
                continue;

            var feats = fc["features"] as JArray ?? new JArray();
            var matched = fc["numberMatched"]?.Value<long>() ?? feats.Count;

            foreach (var feature in feats.OfType<JObject>())
            {
                RewriteItem(feature, ds.Organization.Slug, ds.Slug, collectionId);
                features.Add(feature);
            }

            var consumed = off + feats.Count;

            if (features.Count >= lim)
            {
                if (consumed < matched)
                {
                    resumeDs = i;
                    resumeOffset = (int)consumed;
                }
                else if (i + 1 < datasets.Count)
                {
                    resumeDs = i + 1;
                    resumeOffset = 0;
                }

                break;
            }
        }

        var links = new JArray
        {
            Link("self", SearchUrl(), "application/geo+json"),
            Link("root", StacRoot(), "application/json")
        };

        if (resumeDs >= 0)
        {
            var nextQuery = BuildSearchQuery(request, EncodeToken(resumeDs, resumeOffset), lim);
            links.Add(Link("next", SearchUrl() + nextQuery, "application/geo+json"));
        }

        return new JObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = features,
            ["numberReturned"] = features.Count,
            ["links"] = links
        };
    }

    private JObject SearchByIds(List<Dataset> datasets, string[] ids, string bboxStr, string datetime)
    {
        var wantedIds = new HashSet<string>(ids);
        var features = new JArray();

        foreach (var ds in datasets)
        {
            if (features.Count >= wantedIds.Count)
                break;

            var collectionId = $"{ds.Organization.Slug}/{ds.Slug}";
            var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

            if (ddb.GetStacItemCollection(collectionId, _utils.GenerateDatasetUrl(ds), _utils.GetLocalHost(),
                    bboxStr, datetime, MaxLimit, 0) is not JObject fc)
                continue;

            var feats = fc["features"] as JArray ?? new JArray();
            foreach (var feature in feats.OfType<JObject>())
            {
                RewriteItem(feature, ds.Organization.Slug, ds.Slug, collectionId);
                if (wantedIds.Contains(feature["id"]?.ToString()))
                    features.Add(feature);
            }
        }

        var links = new JArray
        {
            Link("self", SearchUrl(), "application/geo+json"),
            Link("root", StacRoot(), "application/json")
        };

        return new JObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = features,
            ["numberReturned"] = features.Count,
            ["links"] = links
        };
    }

    #endregion

    #region STAC API helpers

    private JToken GetCollectionInternal(string orgSlug, string dsSlug, Guid internalRef)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        var collectionId = $"{orgSlug}/{dsSlug}";
        var ddb = _ddbManager.Get(orgSlug, internalRef);

        if (ddb.GetStac(collectionId, _utils.GenerateDatasetUrl(ds), _utils.GetLocalHost(), null) is not JObject collection)
            return null;

        RewriteCollection(collection, orgSlug, dsSlug);
        return collection;
    }

    private void RewriteCollection(JObject collection, string orgSlug, string dsSlug)
    {
        var collectionUrl = CollectionUrl(orgSlug, dsSlug);

        collection["links"] = new JArray
        {
            Link("root", StacRoot(), "application/json"),
            Link("parent", CollectionsUrl(), "application/json"),
            Link("self", collectionUrl, "application/json"),
            Link("items", ItemsUrl(orgSlug, dsSlug), "application/geo+json")
        };
    }

    private void RewriteItem(JObject feature, string orgSlug, string dsSlug, string collectionId)
    {
        var path = feature["properties"]?["title"]?.Value<string>();

        if (string.IsNullOrEmpty(path) && feature["assets"] is JObject assets)
            path = assets.Properties().FirstOrDefault(p => p.Name != "thumbnail")?.Name;

        if (string.IsNullOrEmpty(path))
            return;

        var featureId = EncodeFeatureId(path);
        var collectionUrl = CollectionUrl(orgSlug, dsSlug);
        var itemUrl = collectionUrl + "/items/" + featureId;

        feature["id"] = featureId;
        feature["collection"] = collectionId;
        feature["links"] = new JArray
        {
            Link("root", StacRoot(), "application/json"),
            Link("parent", collectionUrl, "application/json"),
            Link("collection", collectionUrl, "application/json"),
            Link("self", itemUrl, "application/geo+json")
        };
    }

    private async Task<List<(Dataset ds, SafeMetaManager meta)>> GetAccessibleDatasets()
    {
        var result = new List<(Dataset, SafeMetaManager)>();
        var datasets = _context.Datasets.Include(ds => ds.Organization).ToArray();

        foreach (var ds in datasets)
        {
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
                continue;

            var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);
            var meta = ddb.Meta.GetSafe();
            result.Add((ds, meta));
        }

        return result;
    }

    private static JObject Link(string rel, string href, string type)
    {
        return new JObject
        {
            ["rel"] = rel,
            ["href"] = href,
            ["type"] = type
        };
    }

    private string StacRoot() => _utils.GetLocalHost() + "/stac";
    private string CollectionsUrl() => StacRoot() + "/collections";
    private string CollectionUrl(string orgSlug, string dsSlug) => CollectionsUrl() + $"/{orgSlug}/{dsSlug}";
    private string ItemsUrl(string orgSlug, string dsSlug) => CollectionUrl(orgSlug, dsSlug) + "/items";
    private string SearchUrl() => StacRoot() + "/search";

    private static string BboxToString(double[] bbox)
    {
        // Accept 2D ([minX,minY,maxX,maxY]) or 3D ([minX,minY,minZ,maxX,maxY,maxZ]) bboxes,
        // projecting to the 2D footprint the C++ core understands.
        double[] flat = bbox switch
        {
            { Length: 4 } => bbox,
            { Length: 6 } => new[] { bbox[0], bbox[1], bbox[3], bbox[4] },
            _ => null
        };

        return flat is null
            ? null
            : string.Join(",", flat.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    }

    private static string BuildItemsQuery(double[] bbox, string datetime, int limit, int offset)
    {
        var parts = new List<string> { $"limit={limit}", $"offset={offset}" };

        if (bbox is { Length: 4 } or { Length: 6 })
            parts.Add("bbox=" + Uri.EscapeDataString(
                string.Join(",", bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

        if (!string.IsNullOrEmpty(datetime))
            parts.Add("datetime=" + Uri.EscapeDataString(datetime));

        return "?" + string.Join("&", parts);
    }

    private static string BuildItemsSelfQuery(double[] bbox, string datetime, int? limit, int? offset)
    {
        var parts = new List<string>();

        if (bbox is { Length: 4 } or { Length: 6 })
            parts.Add("bbox=" + Uri.EscapeDataString(
                string.Join(",", bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

        if (!string.IsNullOrEmpty(datetime))
            parts.Add("datetime=" + Uri.EscapeDataString(datetime));

        if (limit.HasValue)
            parts.Add($"limit={limit.Value}");

        if (offset.HasValue)
            parts.Add($"offset={offset.Value}");

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static string BuildSearchQuery(StacSearchRequestDto request, string token, int limit)
    {
        var parts = new List<string> { $"limit={limit}", "token=" + Uri.EscapeDataString(token) };

        if (request.Bbox is { Length: 4 } or { Length: 6 })
            parts.Add("bbox=" + Uri.EscapeDataString(
                string.Join(",", request.Bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

        if (!string.IsNullOrEmpty(request.Datetime))
            parts.Add("datetime=" + Uri.EscapeDataString(request.Datetime));

        if (request.Collections is { Length: > 0 })
            parts.Add("collections=" + Uri.EscapeDataString(string.Join(",", request.Collections)));

        return "?" + string.Join("&", parts);
    }

    private static string EncodeFeatureId(string path)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeFeatureId(string featureId, out string path)
    {
        path = null;

        if (string.IsNullOrEmpty(featureId))
            return false;

        var s = featureId.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 1: return false;
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        try
        {
            path = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeToken(int datasetIndex, int offset)
    {
        var raw = $"{datasetIndex}:{offset}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void DecodeToken(string token, out int datasetIndex, out int offset)
    {
        datasetIndex = 0;
        offset = 0;

        if (string.IsNullOrEmpty(token))
            return;

        try
        {
            var s = token.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            var split = raw.Split(':');
            if (split.Length == 2 &&
                int.TryParse(split[0], out var di) &&
                int.TryParse(split[1], out var off))
            {
                datasetIndex = Math.Max(di, 0);
                offset = Math.Max(off, 0);
            }
        }
        catch (FormatException)
        {
            // Invalid token: start from the beginning
        }
    }

    #endregion

    private static string MakeCacheKey(Dataset ds)
    {
        return $"{CacheKey}-{ds.InternalRef}";
    }

}