#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Stac;

/// <summary>
/// Registry's STAC documents from libddb's data-only ones (XI-09): libddb writes the data (properties, geometry,
/// assets, extent) and Registry every id, collection, link and asset href. Members keep the order clients have
/// always read: libddb writes keys sorted, so a document is sorted again after Registry adds its members.
/// </summary>
public static class StacDocuments
{
    private const string Json = "application/json";
    private const string GeoJson = "application/geo+json";
    private const string ThumbnailAsset = "thumbnail";

    /// <summary>The id of a dataset's Collection in every STAC document.</summary>
    public static string CollectionId(Dataset ds) => $"{ds.Organization.Slug}/{ds.Slug}";

    /// <summary>The path of the entry an Item describes: its title (libddb writes the path there).</summary>
    public static string? ItemPath(JObject item)
    {
        var path = item["properties"]?["title"]?.Value<string>();
        if (string.IsNullOrEmpty(path) && item["assets"] is JObject assets)
            path = assets.Properties().FirstOrDefault(p => p.Name != ThumbnailAsset)?.Name;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    // ---- STAC API 1.0.0 -----------------------------------------------------------------------------------

    /// <summary>A STAC API Item: its feature id, its collection, its links (root, parent, collection, self).</summary>
    public static JObject ApiItem(JObject data, Dataset ds, StacUrls urls)
    {
        var path = ItemPath(data);
        if (path == null)
            return data;

        var collection = urls.Collection(ds.Organization.Slug, ds.Slug);
        data["id"] = StacIds.FeatureId(path);
        data["collection"] = CollectionId(ds);
        data["links"] = new JArray
        {
            Link("root", urls.Root, Json),
            Link("parent", collection, Json),
            Link("collection", collection, Json),
            Link("self", urls.Item(ds.Organization.Slug, ds.Slug, path), GeoJson)
        };
        return WithItemAssetLinks(data, ds, path, urls);
    }

    /// <summary>A STAC API Collection: its id, its links (root, parent, self, items) and its asset links.</summary>
    public static JObject ApiCollection(JObject data, Dataset ds, string datasetFolder, StacUrls urls)
    {
        WithCollectionId(data, ds, datasetFolder);
        data["links"] = new JArray
        {
            Link("root", urls.Root, Json),
            Link("parent", urls.Collections, Json),
            Link("self", urls.Collection(ds.Organization.Slug, ds.Slug), Json),
            Link("items", urls.Items(ds.Organization.Slug, ds.Slug), GeoJson)
        };
        return WithCollectionAssetLinks(data, ds, urls);
    }

    /// <summary>A STAC API ItemCollection: its features as <see cref="ApiItem"/>s, and <paramref name="links"/>.</summary>
    public static JObject ApiItemCollection(JObject data, Dataset ds, JArray links, StacUrls urls)
    {
        data["features"] = new JArray(Features(data).Select(f => ApiItem(f, ds, urls)));
        data["links"] = links;
        return Sorted(data);
    }

    /// <summary>The features of a data-only ItemCollection.</summary>
    public static IEnumerable<JObject> Features(JObject itemCollection) =>
        (itemCollection["features"] as JArray ?? []).OfType<JObject>();

    /// <summary>A link of a STAC API document (members in the order Registry has always written them).</summary>
    public static JObject Link(string rel, string href, string type) => new()
    {
        ["rel"] = rel,
        ["href"] = href,
        ["type"] = type
    };

    // ---- Legacy per-dataset documents (/orgs/{org}/ds/{ds}/stac) -------------------------------------------

    /// <summary>A legacy Item: its slug id, its collection, its links (root, parent, collection, self).</summary>
    public static JObject LegacyItem(JObject data, Dataset ds, StacUrls urls)
    {
        var path = ItemPath(data);
        if (path == null)
            return data;

        var collection = urls.LegacyCollection(ds);
        data["id"] = StacIds.LegacyItemId(path);
        data["collection"] = CollectionId(ds);
        data["links"] = new JArray
        {
            LegacyLink("root", urls.Root, Json),
            LegacyLink("parent", collection, Json),
            LegacyLink("collection", collection, Json),
            LegacyLink("self", urls.LegacyItem(ds, path), GeoJson)
        };
        return WithItemAssetLinks(data, ds, path, urls);
    }

    /// <summary>
    /// A legacy Collection: its id, its links (root, parent, self, then one item link per entry with a geometry,
    /// in path order) and its asset links.
    /// </summary>
    public static JObject LegacyCollection(JObject data, Dataset ds, string datasetFolder,
        IEnumerable<string> itemPaths, StacUrls urls)
    {
        WithCollectionId(data, ds, datasetFolder);
        var links = new JArray
        {
            LegacyLink("root", urls.Root, Json),
            LegacyLink("parent", urls.Root, Json),
            LegacyLink("self", urls.LegacyCollection(ds), Json)
        };
        foreach (var path in itemPaths)
            links.Add(new JObject
            {
                ["href"] = urls.LegacyItem(ds, path),
                ["rel"] = "item",
                ["title"] = path,
                ["type"] = GeoJson
            });
        data["links"] = links;
        return WithCollectionAssetLinks(data, ds, urls);
    }

    // A legacy link: members sorted, as libddb wrote them
    private static JObject LegacyLink(string rel, string href, string type) => new()
    {
        ["href"] = href,
        ["rel"] = rel,
        ["type"] = type
    };

    // ---- Shared ---------------------------------------------------------------------------------------------

    // libddb names an unnamed Collection after its dataset folder (the dataset's internal reference): Registry
    // names it after its id, as it always did, and so its description when there is no readme
    private static void WithCollectionId(JObject data, Dataset ds, string datasetFolder)
    {
        var id = CollectionId(ds);
        data["id"] = id;
        foreach (var key in new[] { "title", "description" })
            if (data[key]?.Value<string>() == datasetFolder)
                data[key] = id;
    }

    // The entry's file and its thumbnail
    private static JObject WithItemAssetLinks(JObject item, Dataset ds, string path, StacUrls urls)
    {
        if (item["assets"] is JObject assets)
            foreach (var asset in assets.Properties().ToList())
                if (asset.Value is JObject a)
                {
                    a["href"] = asset.Name == ThumbnailAsset ? urls.Thumbnail(ds, path) : urls.Download(ds, asset.Name);
                    asset.Value = Sorted(a);
                }

        return Sorted(item);
    }

    // The dataset's files without a geometry
    private static JObject WithCollectionAssetLinks(JObject collection, Dataset ds, StacUrls urls)
    {
        if (collection["assets"] is JObject assets)
            foreach (var asset in assets.Properties().ToList())
                if (asset.Value is JObject a)
                {
                    a["href"] = urls.Download(ds, asset.Name);
                    asset.Value = Sorted(a);
                }

        return Sorted(collection);
    }

    /// <summary>The object with its members in ordinal order (libddb's).</summary>
    private static JObject Sorted(JObject o) =>
        new(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).ToList());

    /// <summary>The folder name of a dataset's index (libddb's fallback title of an unnamed Collection).</summary>
    public static string FolderOf(IDDB ddb) => Path.GetFileName(Path.TrimEndingDirectorySeparator(ddb.DatasetFolderPath));
}
