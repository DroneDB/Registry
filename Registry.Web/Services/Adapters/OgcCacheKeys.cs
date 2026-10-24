#nullable enable
using System.Threading.Tasks;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IOgcCacheKeys"/>: one cache generation per dataset (<see cref="CacheGenerations"/>, shared
/// by every instance); the key formats are <see cref="CacheCategories"/>'.
/// </summary>
public sealed class OgcCacheKeys(CacheGenerations generations) : IOgcCacheKeys
{
    public async Task<string> ForAsync(string orgSlug, string dsSlug, string kind, string? detail = null,
        System.Threading.CancellationToken ct = default) =>
        CacheCategories.OgcDocument(orgSlug, dsSlug,
            await generations.CurrentAsync(CacheCategories.OgcGeneration(orgSlug, dsSlug), ct), kind, detail);

    public Task InvalidateAsync(string orgSlug, string dsSlug) =>
        generations.RenewAsync(CacheCategories.OgcGeneration(orgSlug, dsSlug));
}
