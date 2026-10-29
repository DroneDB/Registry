using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Services.Caching;

/// <summary>
/// Generations of cache scopes: every key of a scope carries the scope's current generation, so invalidating
/// them all is one write of a new generation, with no key scan, on every cache provider. A
/// generation is a random token: two concurrent invalidations cannot cancel each other out, and the entries of
/// an old generation expire on their own expiry.
/// </summary>
public sealed class CacheGenerations(IDistributedCache cache, ILogger<CacheGenerations> logger)
{
    // Long enough to outlive every entry of a scope (minutes); renewed by each read
    private static readonly DistributedCacheEntryOptions GenerationOptions = new()
    {
        SlidingExpiration = TimeSpan.FromDays(7)
    };

    /// <summary>The current generation of the scope whose generation is stored at <paramref name="generationKey"/>.</summary>
    public async Task<string> CurrentAsync(string generationKey, CancellationToken ct = default)
    {
        // A read that fails gives a new token: the scope's entries miss
        var unreadable = NewGeneration();
        var generation = await FailOpen.RunAsync(() => cache.GetStringAsync(generationKey, ct), unreadable, logger,
            "generation read", generationKey, ct);
        if (generation != null)
            return generation;

        // First use, or the token expired: either way no entry of an older token can be reached
        var created = NewGeneration();
        await TryWriteAsync(generationKey, created, ct);
        return created;
    }

    /// <summary>Makes every key of the scope built so far unreachable.</summary>
    public Task RenewAsync(string generationKey, CancellationToken ct = default) =>
        TryWriteAsync(generationKey, NewGeneration(), ct);

    private Task TryWriteAsync(string generationKey, string generation, CancellationToken ct) =>
        FailOpen.RunAsync(() => cache.SetStringAsync(generationKey, generation, GenerationOptions, ct), logger,
            "generation write", generationKey, ct);

    private static string NewGeneration() => Guid.NewGuid().ToString("N")[..12];
}
