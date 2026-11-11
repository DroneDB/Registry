using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Stac;

namespace Registry.Web.Services.Managers;

/// <summary>
/// STAC: the STAC API 1.0.0 (landing page, collections, items, search) and the legacy per-dataset documents.
/// libddb gives the data of each document (<c>DDBStacData</c>, <c>DDBStacItemCollectionData</c>), and
/// <see cref="StacDocuments"/> adds every id, link and href; the listings are <see cref="StacCatalog"/>'s.
/// </summary>
public class StacManager
{
    // These could end up in the config
    private const string CatalogTitle = "DroneDB public datasets catalog";
    private const string CatalogId = "DroneDB Catalog";
    private const string StacVersion = "1.1.0";
    private const string Json = "application/json";
    private const string GeoJson = "application/geo+json";

    // STAC API 1.0.0 conformance classes implemented by this server
    private static readonly string[] ConformanceClasses =
    [
        "https://api.stacspec.org/v1.0.0/core",
        "https://api.stacspec.org/v1.0.0/collections",
        "https://api.stacspec.org/v1.0.0/ogcapi-features",
        "https://api.stacspec.org/v1.0.0/item-search",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson"
    ];

    private const int DefaultLimit = 10;
    private const int MaxLimit = 10000;

    private const int DefaultCollectionsLimit = 10;
    private const int MaxCollectionsLimit = 100;

    private readonly IDatasetAccessResolver _access;
    private readonly IDdbManager _ddbManager;
    private readonly StacCatalog _catalog;
    private readonly StacUrls _urls;
    private readonly ILogger<StacManager> _logger;

    public StacManager(IDatasetAccessResolver access, IDdbManager ddbManager, StacCatalog catalog, StacUrls urls,
        ILogger<StacManager> logger)
    {
        _access = access;
        _ddbManager = ddbManager;
        _catalog = catalog;
        _urls = urls;
        _logger = logger;
    }

    public async Task<JsonNode> GetStacChild(string orgSlug, string dsSlug, string? path = null,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var ds = access.Dataset;
        var ddb = access.Ddb;

        _logger.LogInformation("In GetStacChild('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

        if (path != null && !ddb.EntryExists(path))
            throw new BadRequestException("Entry does not exist");

        if (path != null)
            return StacDocuments.LegacyItem(ItemData(ddb, path), ds, _urls);

        // The Collection links every entry with a geometry, in path order: the data-only item collection
        // lists exactly those
        var items = ItemCollectionData(ddb, null, null, int.MaxValue, 0);
        var itemPaths = StacDocuments.Features(items).Select(StacDocuments.ItemPath).OfType<string>();
        return StacDocuments.LegacyCollection(CollectionData(ddb), ds, StacDocuments.FolderOf(ddb), itemPaths, _urls);
    }

    #region STAC API 1.0.0

    public Task<StacCatalogDto> GetLandingPage(CancellationToken ct = default) =>
        _catalog.ListingAsync(CacheCategories.StacLanding, BuildLandingPage, ct);

    private async Task<StacCatalogDto> BuildLandingPage(CancellationToken ct)
    {
        var links = new List<StacLinkDto>
        {
            new() { Href = _urls.Root, Relationship = "self", Type = Json, Title = CatalogTitle },
            new() { Href = _urls.Root, Relationship = "root", Type = Json, Title = CatalogTitle },
            new() { Href = _urls.Conformance, Relationship = "conformance", Type = Json },
            new() { Href = _urls.Collections, Relationship = "data", Type = Json },
            new() { Href = _urls.Search, Relationship = "search", Type = GeoJson, Method = "GET" },
            new() { Href = _urls.Search, Relationship = "search", Type = GeoJson, Method = "POST" },
            new() { Href = _urls.ServiceDescription, Relationship = "service-desc", Type = Json },
            new() { Href = _urls.ServiceDocumentation, Relationship = "service-doc", Type = "text/html" }
        };

        foreach (var ds in await _catalog.ListedDatasetsAsync(ct))
        {
            links.Add(new StacLinkDto
            {
                Href = _urls.Collection(ds.Organization.Slug, ds.Slug),
                Relationship = "child",
                Type = Json,
                Title = await _catalog.NameAsync(ds, ct)
            });
        }

        return new StacCatalogDto
        {
            Type = "Catalog",
            StacVersion = StacVersion,
            Id = CatalogId,
            Description = CatalogTitle,
            ConformsTo = ConformanceClasses,
            Links = links
        };
    }

    public StacConformanceDto GetConformance()
    {
        return new StacConformanceDto { ConformsTo = ConformanceClasses };
    }

    public Task<StacCollectionsDto> GetCollections(int? limit = null, int? offset = null,
        CancellationToken ct = default)
    {
        // Every collection embeds its asset list (tens of KB each), so the listing is paged
        var lim = Math.Clamp(limit ?? DefaultCollectionsLimit, 1, MaxCollectionsLimit);
        var off = Math.Max(offset ?? 0, 0);

        return _catalog.ListingAsync((generation, user) => CacheCategories.StacCollections(generation, user, lim, off),
            token => BuildCollections(limit, offset, lim, off, token), ct);
    }

    private async Task<StacCollectionsDto> BuildCollections(int? limit, int? offset, int lim, int off,
        CancellationToken ct)
    {
        var datasets = await _catalog.ListedDatasetsAsync(ct);

        var collections = new List<JsonNode>();
        foreach (var ds in datasets.Skip(off).Take(lim))
        {
            // One native read per dataset: nobody waits for the listing any more, stop
            ct.ThrowIfCancellationRequested();

            // One unreadable dataset index must not take the whole listing down
            try
            {
                collections.Add(Collection(ds, _ddbManager.Get(ds.Organization.Slug, ds.InternalRef)));
            }
            catch (Exception ex) when (!DdbErrors.IsTransient(ex))
            {
                _logger.LogWarning(ex, "Skipping STAC collection {OrgSlug}/{DsSlug}",
                    ds.Organization.Slug, ds.Slug);
            }
        }

        var links = new List<StacLinkDto>
        {
            new() { Href = _urls.Collections + StacQueries.BuildItemsSelfQuery(null, null, limit, offset), Relationship = "self", Type = Json },
            new() { Href = _urls.Root, Relationship = "root", Type = Json },
            new() { Href = _urls.Root, Relationship = "parent", Type = Json }
        };

        if (off + lim < datasets.Count)
            links.Add(new StacLinkDto
            {
                Href = _urls.Collections + StacQueries.BuildItemsQuery(null, null, lim, off + lim),
                Relationship = "next", Type = Json
            });

        if (off > 0)
            links.Add(new StacLinkDto
            {
                Href = _urls.Collections + StacQueries.BuildItemsQuery(null, null, lim, Math.Max(off - lim, 0)),
                Relationship = "prev", Type = Json
            });

        return new StacCollectionsDto
        {
            Collections = collections,
            Links = links,
            NumberMatched = datasets.Count,
            NumberReturned = collections.Count
        };
    }

    public async Task<JsonNode> GetCollection(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        return Collection(access.Dataset, access.Ddb);
    }

    public async Task<JsonNode> GetCollectionItems(string orgSlug, string dsSlug, double[]? bbox = null,
        string? datetime = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var ds = access.Dataset;

        var lim = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var off = Math.Max(offset ?? 0, 0);

        var data = ItemCollectionData(access.Ddb, StacQueries.BboxToString(bbox), datetime, lim, off);
        var returned = StacDocuments.Features(data).Count();
        var numberMatched = StacDocuments.Whole(data["numberMatched"]) ?? returned;
        var items = _urls.Items(orgSlug, dsSlug);

        // The self link must match the requested URL exactly, so only echo back the
        // query parameters the client actually provided (STAC API conformance).
        var links = new JsonArray
        {
            StacDocuments.Link("self", items + StacQueries.BuildItemsSelfQuery(bbox, datetime, limit, offset), GeoJson),
            StacDocuments.Link("root", _urls.Root, Json),
            StacDocuments.Link("collection", _urls.Collection(orgSlug, dsSlug), Json)
        };

        if (off + returned < numberMatched)
            links.Add(StacDocuments.Link("next", items + StacQueries.BuildItemsQuery(bbox, datetime, lim, off + lim),
                GeoJson));

        return StacDocuments.ApiItemCollection(data, ds, links, _urls);
    }

    public async Task<JsonNode> GetCollectionItem(string orgSlug, string dsSlug, string featureId,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

        // An undecodable feature id simply identifies a feature that does not exist (404),
        // not a malformed request (400).
        if (!StacIds.TryDecodePath(featureId, out var path) || !access.Ddb.EntryExists(path))
            throw new NotFoundException("Feature does not exist");

        return StacDocuments.ApiItem(ItemData(access.Ddb, path), access.Dataset, _urls);
    }

    public async Task<JsonNode> Search(StacSearchRequestDto? request, CancellationToken ct = default)
    {
        request ??= new StacSearchRequestDto();

        var lim = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit);
        var bboxStr = StacQueries.BboxToString(request.Bbox);
        var datetime = request.Datetime;

        // A search over every collection covers the listed ones; one that names its collections reaches an
        // unlisted collection too, as its URL does
        List<Dataset> datasets;
        if (request.Collections is { Length: > 0 })
        {
            var wanted = new HashSet<string>(request.Collections);
            datasets = [.. (await _catalog.ReadableDatasetsAsync(ct)).Where(d => wanted.Contains(StacDocuments.CollectionId(d)))];
        }
        else
            datasets = await _catalog.ListedDatasetsAsync(ct);

        if (request.Ids is { Length: > 0 })
            return SearchByIds(datasets, request.Ids, bboxStr, datetime, ct);

        StacQueries.DecodeToken(request.Token, out var startDs, out var startOffset);

        var features = new JsonArray();
        var resumeDs = -1;
        var resumeOffset = 0;

        for (var i = startDs; i < datasets.Count; i++)
        {
            // One native query per dataset: a client that went away stops the search
            ct.ThrowIfCancellationRequested();
            var ds = datasets[i];
            var off = i == startDs ? startOffset : 0;
            var need = lim - features.Count;

            var data = ItemCollectionData(Ddb(ds), bboxStr, datetime, need, off);
            var page = StacDocuments.TakeFeatures(data);
            var matched = StacDocuments.Whole(data["numberMatched"]) ?? page.Count;

            foreach (var feature in page)
                features.Add(StacDocuments.ApiItem(feature, ds, _urls));

            var consumed = off + page.Count;

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

        var links = new JsonArray
        {
            StacDocuments.Link("self", _urls.Search, GeoJson),
            StacDocuments.Link("root", _urls.Root, Json)
        };

        if (resumeDs >= 0)
        {
            var nextQuery = StacQueries.BuildSearchQuery(request, StacQueries.EncodeToken(resumeDs, resumeOffset), lim);
            links.Add(StacDocuments.Link("next", _urls.Search + nextQuery, GeoJson));
        }

        return FeatureCollection(features, links);
    }

    private JsonObject SearchByIds(List<Dataset> datasets, string[] ids, string? bboxStr, string? datetime,
        CancellationToken ct)
    {
        var wantedIds = new HashSet<string>(ids);
        var features = new JsonArray();

        foreach (var ds in datasets)
        {
            ct.ThrowIfCancellationRequested();
            if (features.Count >= wantedIds.Count)
                break;

            var data = ItemCollectionData(Ddb(ds), bboxStr, datetime, MaxLimit, 0);
            foreach (var feature in StacDocuments.TakeFeatures(data))
            {
                var item = StacDocuments.ApiItem(feature, ds, _urls);
                if (wantedIds.Contains(StacDocuments.Text(item["id"]) ?? ""))
                    features.Add(item);
            }
        }

        return FeatureCollection(features, new JsonArray
        {
            StacDocuments.Link("self", _urls.Search, GeoJson),
            StacDocuments.Link("root", _urls.Root, Json)
        });
    }

    #endregion

    private static JsonObject FeatureCollection(JsonArray features, JsonArray links) => new()
    {
        ["type"] = "FeatureCollection",
        ["features"] = features,
        ["numberReturned"] = features.Count,
        ["links"] = links
    };

    private IDDB Ddb(Dataset ds) => _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

    private JsonObject Collection(Dataset ds, IDDB ddb) =>
        StacDocuments.ApiCollection(CollectionData(ddb), ds, StacDocuments.FolderOf(ddb), _urls);

    private static JsonObject CollectionData(IDDB ddb) =>
        StacDocuments.Mutable(ddb.GetStacData(), "STAC collection");

    private static JsonObject ItemData(IDDB ddb, string path) =>
        StacDocuments.Mutable(ddb.GetStacData(path), "STAC item");

    private static JsonObject ItemCollectionData(IDDB ddb, string? bbox, string? datetime, int limit, int offset) =>
        StacDocuments.Mutable(ddb.GetStacItemCollectionData(bbox, datetime, limit, offset), "STAC item collection");
}
