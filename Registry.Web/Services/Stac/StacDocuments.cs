using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Stac;

/// <summary>
/// Registry's STAC documents from libddb's data-only ones: libddb writes the data (properties, geometry,
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
    public static string? ItemPath(JsonObject item)
    {
        var path = Text((item["properties"] as JsonObject)?["title"]);
        if (string.IsNullOrEmpty(path) && item["assets"] is JsonObject assets)
            path = assets.Select(a => a.Key).FirstOrDefault(name => name != ThumbnailAsset);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>
    /// The 2D footprint <c>[minX, minY, maxX, maxY]</c> of a 2D or 3D (<c>[minX, minY, minZ, maxX, maxY, maxZ]</c>) bbox,
    /// the form the C++ core understands; null for any other length.
    /// </summary>
    public static double[]? FlattenBbox(double[]? bbox) => bbox switch
    {
        { Length: 4 } => bbox,
        { Length: 6 } => [bbox[0], bbox[1], bbox[3], bbox[4]],
        _ => null
    };

    /// <summary>
    /// The footprint (<see cref="FlattenBbox"/>) a Collection document gives in <c>extent.spatial.bbox[0]</c>, as libddb
    /// writes it (an empty dataset has all zeros); null when the document has none or it is not made of numbers.
    /// </summary>
    public static double[]? SpatialBbox(JsonElement collection)
    {
        if (collection.ValueKind != JsonValueKind.Object ||
            !collection.TryGetProperty("extent", out var extent) || extent.ValueKind != JsonValueKind.Object ||
            !extent.TryGetProperty("spatial", out var spatial) || spatial.ValueKind != JsonValueKind.Object ||
            !spatial.TryGetProperty("bbox", out var boxes) || boxes.ValueKind != JsonValueKind.Array ||
            boxes.GetArrayLength() == 0 || boxes[0].ValueKind != JsonValueKind.Array)
            return null;

        var values = new List<double>();
        foreach (var value in boxes[0].EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
                return null;
            values.Add(number);
        }

        return FlattenBbox(values.ToArray());
    }

    /// <summary>The document libddb returned as a mutable object (<see cref="JsonElement"/> is read-only).</summary>
    public static JsonObject Mutable(JsonElement document, string what) =>
        document.ValueKind == JsonValueKind.Object ? JsonObject.Create(document)! : throw new DdbException($"Invalid {what} result");

    /// <summary>The text of a string node, else null.</summary>
    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The whole number of a number node, else null.</summary>
    public static long? Whole(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    // ---- STAC API 1.0.0 -----------------------------------------------------------------------------------

    /// <summary>A STAC API Item: its feature id, its collection, its links (root, parent, collection, self).</summary>
    public static JsonObject ApiItem(JsonObject data, Dataset ds, StacUrls urls)
    {
        var path = ItemPath(data);
        if (path == null)
            return data;

        var collection = urls.Collection(ds.Organization.Slug, ds.Slug);
        data["id"] = StacIds.FeatureId(path);
        data["collection"] = CollectionId(ds);
        data["links"] = new JsonArray
        {
            Link("root", urls.Root, Json),
            Link("parent", collection, Json),
            Link("collection", collection, Json),
            Link("self", urls.Item(ds.Organization.Slug, ds.Slug, path), GeoJson)
        };
        return WithItemAssetLinks(data, ds, path, urls);
    }

    /// <summary>A STAC API Collection: its id, its links (root, parent, self, items) and its asset links.</summary>
    public static JsonObject ApiCollection(JsonObject data, Dataset ds, string datasetFolder, StacUrls urls)
    {
        WithCollectionId(data, ds, datasetFolder);
        data["links"] = new JsonArray
        {
            Link("root", urls.Root, Json),
            Link("parent", urls.Collections, Json),
            Link("self", urls.Collection(ds.Organization.Slug, ds.Slug), Json),
            Link("items", urls.Items(ds.Organization.Slug, ds.Slug), GeoJson)
        };
        return WithCollectionAssetLinks(data, ds, urls);
    }

    /// <summary>A STAC API ItemCollection: its features as <see cref="ApiItem"/>s, and <paramref name="links"/>.</summary>
    public static JsonObject ApiItemCollection(JsonObject data, Dataset ds, JsonArray links, StacUrls urls)
    {
        // The features are rewritten where they are: an item keeps its place in the collection
        foreach (var feature in Features(data).ToList())
            ApiItem(feature, ds, urls);
        data["links"] = links;
        return Sorted(data);
    }

    /// <summary>The features of a data-only ItemCollection (still members of it).</summary>
    public static IEnumerable<JsonObject> Features(JsonObject itemCollection) =>
        (itemCollection["features"] as JsonArray)?.OfType<JsonObject>() ?? [];

    /// <summary>
    /// The features of a data-only ItemCollection taken out of it, to be members of another document (a node has one parent).
    /// </summary>
    public static List<JsonObject> TakeFeatures(JsonObject itemCollection)
    {
        if (itemCollection["features"] is not JsonArray array)
            return [];

        var features = array.OfType<JsonObject>().ToList();
        array.Clear();
        return features;
    }

    /// <summary>A link of a STAC API document (members in the order Registry has always written them).</summary>
    public static JsonObject Link(string rel, string href, string type) => new()
    {
        ["rel"] = rel,
        ["href"] = href,
        ["type"] = type
    };

    // ---- Legacy per-dataset documents (/orgs/{org}/ds/{ds}/stac) -------------------------------------------

    /// <summary>A legacy Item: its slug id, its collection, its links (root, parent, collection, self).</summary>
    public static JsonObject LegacyItem(JsonObject data, Dataset ds, StacUrls urls)
    {
        var path = ItemPath(data);
        if (path == null)
            return data;

        var collection = urls.LegacyCollection(ds);
        data["id"] = StacIds.LegacyItemId(path);
        data["collection"] = CollectionId(ds);
        data["links"] = new JsonArray
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
    public static JsonObject LegacyCollection(JsonObject data, Dataset ds, string datasetFolder,
        IEnumerable<string> itemPaths, StacUrls urls)
    {
        WithCollectionId(data, ds, datasetFolder);
        var links = new JsonArray
        {
            LegacyLink("root", urls.Root, Json),
            LegacyLink("parent", urls.Root, Json),
            LegacyLink("self", urls.LegacyCollection(ds), Json)
        };
        foreach (var path in itemPaths)
            links.Add(new JsonObject
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
    private static JsonObject LegacyLink(string rel, string href, string type) => new()
    {
        ["href"] = href,
        ["rel"] = rel,
        ["type"] = type
    };

    // ---- Shared ---------------------------------------------------------------------------------------------

    // libddb names an unnamed Collection after its dataset folder (the dataset's internal reference): Registry
    // names it after its id, as it always did, and so its description when there is no readme
    private static void WithCollectionId(JsonObject data, Dataset ds, string datasetFolder)
    {
        var id = CollectionId(ds);
        data["id"] = id;
        foreach (var key in new[] { "title", "description" })
            if (Text(data[key]) == datasetFolder)
                data[key] = id;
    }

    // The entry's file and its thumbnail
    private static JsonObject WithItemAssetLinks(JsonObject item, Dataset ds, string path, StacUrls urls)
    {
        if (item["assets"] is JsonObject assets)
            foreach (var asset in assets.ToList())
                if (asset.Value is JsonObject a)
                {
                    a["href"] = asset.Key == ThumbnailAsset ? urls.Thumbnail(ds, path) : urls.Download(ds, asset.Key);
                    Sorted(a);
                }

        return Sorted(item);
    }

    // The dataset's files without a geometry
    private static JsonObject WithCollectionAssetLinks(JsonObject collection, Dataset ds, StacUrls urls)
    {
        if (collection["assets"] is JsonObject assets)
            foreach (var asset in assets.ToList())
                if (asset.Value is JsonObject a)
                {
                    a["href"] = urls.Download(ds, asset.Key);
                    Sorted(a);
                }

        return Sorted(collection);
    }

    /// <summary>The object with its members in ordinal order (libddb's), in place.</summary>
    private static JsonObject Sorted(JsonObject o)
    {
        var members = o.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        o.Clear();
        foreach (var (key, value) in members)
            o[key] = value;
        return o;
    }

    /// <summary>The folder name of a dataset's index (libddb's fallback title of an unnamed Collection).</summary>
    public static string FolderOf(IDDB ddb) => Path.GetFileName(Path.TrimEndingDirectorySeparator(ddb.DatasetFolderPath));
}
