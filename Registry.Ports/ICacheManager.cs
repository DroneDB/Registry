using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface ICacheManager
{
    /// <param name="seed">Name of the cache.</param>
    /// <param name="getData">Generates an entry on a miss.</param>
    /// <param name="expiration">Sliding expiration in the distributed cache (default 30 minutes).</param>
    /// <param name="localExpiration">When set, entries are also kept in this process's memory for this long, so a
    /// hot entry costs no distributed-cache round trip. <see cref="RemoveAsync"/> and
    /// <see cref="RemoveByCategoryAsync"/> drop them here at once; other processes see a change when their copy
    /// expires.</param>
    void Register(string seed, Func<object[], Task<byte[]>> getData, TimeSpan? expiration = null,
        TimeSpan? localExpiration = null);

    /// <summary>
    /// Registers a provider that takes a token (RQ-13): canceled when every caller waiting for the generation
    /// has cancelled (concurrent misses share one generation), never while one still waits.
    /// </summary>
    /// <inheritdoc cref="Register(string, Func{object[], Task{byte[]}}, TimeSpan?, TimeSpan?)"/>
    void Register(string seed, Func<object[], CancellationToken, Task<byte[]>> getData, TimeSpan? expiration = null,
        TimeSpan? localExpiration = null);

    void Unregister(string seed);

    /// <summary>The entry of <paramref name="parameters"/>, generated on a miss; nobody can cancel the wait.</summary>
    Task<byte[]> GetAsync(string seed, string category, params object[] parameters);

    /// <summary>
    /// The entry of <paramref name="parameters"/>, generated on a miss. <paramref name="ct"/> ends this caller's
    /// wait; the generation, shared by the concurrent callers of the key, stops when all of them have gone.
    /// </summary>
    Task<byte[]> GetAsync(string seed, string category, CancellationToken ct, params object[] parameters);

    Task SetAsync(string seed, string category, byte[] data, params object[] parameters);

    Task RemoveAsync(string seed, string category, params object[] parameters);

    /// <summary>
    /// Removes all cache entries matching the given seed and category prefix.
    /// For Redis: uses SCAN + DELETE for pattern-based removal.
    /// For InMemory: no-op (cache entries will expire naturally).
    /// </summary>
    Task RemoveByCategoryAsync(string seed, string category);

    bool IsRegistered(string seed);
}