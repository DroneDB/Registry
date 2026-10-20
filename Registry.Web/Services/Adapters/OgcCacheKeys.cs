#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IOgcCacheKeys"/>: the generation is a random token stored in the
/// distributed cache (shared by every instance). A new token replaces it on invalidation, so two
/// concurrent invalidations cannot cancel each other out; the old entries expire on their TTL.
/// </summary>
public sealed class OgcCacheKeys(IDistributedCache cache) : IOgcCacheKeys
{
    // Long enough to outlive every OGC entry (minutes); renewed by each read
    private static readonly DistributedCacheEntryOptions GenerationOptions = new()
    {
        SlidingExpiration = TimeSpan.FromDays(7)
    };

    public async Task<string> ForAsync(string orgSlug, string dsSlug, string kind, string? detail = null)
    {
        var generation = await cache.GetStringAsync(GenerationKey(orgSlug, dsSlug));
        if (generation == null)
        {
            // First use, or the token expired: either way no entry of an older token can be reached
            generation = NewGeneration();
            await cache.SetStringAsync(GenerationKey(orgSlug, dsSlug), generation, GenerationOptions);
        }

        return $"ogc-{kind}-{orgSlug}-{dsSlug}-g{generation}-{detail ?? string.Empty}";
    }

    public Task InvalidateAsync(string orgSlug, string dsSlug) =>
        cache.SetStringAsync(GenerationKey(orgSlug, dsSlug), NewGeneration(), GenerationOptions);

    private static string GenerationKey(string orgSlug, string dsSlug) => $"ogc-gen-{orgSlug}-{dsSlug}";

    private static string NewGeneration() => Guid.NewGuid().ToString("N")[..12];
}
