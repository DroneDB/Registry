#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Short-lived cache of dataset lookups by slug (REG-05 item 3): every request resolves its dataset
/// (and organization) by slug, often twice. Entries are dropped when a dataset or organization row
/// changes (see <c>DatasetChangeInterceptor</c>) and expire after a few seconds otherwise.
/// </summary>
public interface IDatasetLookupCache
{
    /// <summary>
    /// The dataset of <paramref name="orgSlug"/>/<paramref name="dsSlug"/> (with its organization),
    /// from the cache or from <paramref name="load"/>. Never shared: each call gets its own copy.
    /// Null results are not cached.
    /// </summary>
    Task<Dataset?> GetOrLoadAsync(string orgSlug, string dsSlug, Func<CancellationToken, Task<Dataset?>> load,
        CancellationToken ct = default);

    /// <summary>Drops the cached lookup of one dataset.</summary>
    void InvalidateDataset(string orgSlug, string dsSlug);

    /// <summary>Drops the cached lookups of every dataset of an organization.</summary>
    void InvalidateOrganization(string orgSlug);

    /// <summary>Drops every cached lookup.</summary>
    void InvalidateAll();
}
