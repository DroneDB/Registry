#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;

namespace Registry.Web.Models;

/// <summary>
/// A page of an organization's dataset list: search, sort and paging done by the server,
/// so the per-dataset work that only the page needs (the thumbnail check) runs for the page only.
/// </summary>
/// <param name="Skip">Datasets to skip after filtering and sorting.</param>
/// <param name="Take">Page size; null = every dataset.</param>
/// <param name="Query">Case-insensitive text matched against name, slug and tagline.</param>
/// <param name="Sort">name (default), creationDate, size, entries or visibility.</param>
/// <param name="Descending">Sort direction.</param>
/// <param name="Slugs">Only these datasets (the Hub asks for the thumbnails of the rows it shows).</param>
public sealed record DatasetListQuery(int Skip = 0, int? Take = null, string? Query = null, string? Sort = null,
    bool Descending = false, IReadOnlyCollection<string>? Slugs = null)
{
    public const int MaxTake = 500;

    public IEnumerable<DatasetDto> Apply(IEnumerable<DatasetDto> datasets)
    {
        if (Slugs is { Count: > 0 })
        {
            var wanted = new HashSet<string>(Slugs, StringComparer.Ordinal);
            datasets = datasets.Where(d => wanted.Contains(d.Slug));
        }

        var filtered = string.IsNullOrWhiteSpace(Query)
            ? datasets
            : datasets.Where(d => Matches(d, Query.Trim()));

        IOrderedEnumerable<DatasetDto> ordered = (Sort ?? "name").ToLowerInvariant() switch
        {
            "creationdate" => Order(filtered, d => d.CreationDate),
            "size" => Order(filtered, d => d.Size),
            "entries" => Order(filtered, Entries),
            "visibility" => Order(filtered, d => (int)VisibilityOf(d)),
            _ => Descending
                ? filtered.OrderByDescending(NameOf, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase)
        };

        // Stable among equal keys: by slug
        var page = ordered.ThenBy(d => d.Slug, StringComparer.Ordinal).Skip(Math.Max(0, Skip));
        return Take is { } take ? page.Take(Math.Clamp(take, 1, MaxTake)) : page;
    }

    private IOrderedEnumerable<DatasetDto> Order<TKey>(IEnumerable<DatasetDto> source, Func<DatasetDto, TKey> key) =>
        Descending ? source.OrderByDescending(key) : source.OrderBy(key);

    private static bool Matches(DatasetDto d, string query) =>
        NameOf(d).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        d.Slug.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        (Meta(d, "tagline")?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    /// <summary>The display name the Hub shows: the name meta, else the slug.</summary>
    public static string NameOf(DatasetDto d) => Meta(d, "name") is { Length: > 0 } name ? name : d.Slug;

    private static long Entries(DatasetDto d) =>
        d.Properties != null && d.Properties.TryGetValue("entries", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private static Visibility VisibilityOf(DatasetDto d) =>
        MetaToken(d, "visibility") is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var visibility)
            ? (Visibility)visibility
            : Visibility.Private;

    private static string? Meta(DatasetDto d, string key) =>
        MetaToken(d, key) is { } token ? token.ValueKind == JsonValueKind.String ? token.GetString() : token.GetRawText() : null;

    private static JsonElement? MetaToken(DatasetDto d, string key) =>
        d.Properties != null && d.Properties.TryGetValue("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
        meta.TryGetProperty(key, out var entry) && entry.ValueKind == JsonValueKind.Object &&
        entry.TryGetProperty("data", out var data)
            ? data
            : null;
}
