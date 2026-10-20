#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Registry.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Distributed cache wrapper with registration/unregistration of data providers and pattern-based invalidation.
/// Concurrent misses for the same key share one generation (single-flight), and cache read/write
/// failures fall back to generating the data instead of failing the request (fail-open).
/// Seeds registered with a local expiration also keep their entries in process memory.
/// </summary>
public class CacheManager : ICacheManager
{
    private class Carrier
    {
        public required Func<object[], Task<byte[]>> GetDataAsync { get; init; }
        public TimeSpan Expiration { get; init; }
        public TimeSpan LocalExpiration { get; init; }
    }

    /// <summary>
    /// One in-progress generation for a key. Invalidating the key detaches the flight from
    /// <see cref="_inFlight"/> (new callers start a fresh generation) and marks it so its
    /// result, computed from pre-invalidation state, is not written to the cache.
    /// </summary>
    private sealed class Flight
    {
        private readonly TaskCompletionSource<byte[]> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _invalidated;

        public Task<byte[]> Completion => _completion.Task;
        public bool Invalidated => _invalidated;
        public void Invalidate() => _invalidated = true;
        public void Complete(byte[] data) => _completion.SetResult(data);
        public void Fail(Exception ex) => _completion.SetException(ex);
    }

    private readonly IDistributedCache _cache;
    private readonly ILogger<CacheManager> _logger;
    private readonly ICacheKeyScanner _keyScanner;

    private readonly TimeSpan _defaultCacheExpiration = new(0, 30, 0);

    private readonly ConcurrentDictionary<string, Carrier> _providers = new();
    private readonly ConcurrentDictionary<string, Flight> _inFlight = new();

    // Process-memory tier of the seeds registered with a local expiration. A category's entries
    // share a token that RemoveByCategoryAsync cancels.
    private readonly IMemoryCache? _local;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _localCategories = new();

    public CacheManager(IDistributedCache cache, ILogger<CacheManager> logger,
        ICacheKeyScanner keyScanner, IMemoryCache? localCache = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyScanner = keyScanner ?? throw new ArgumentNullException(nameof(keyScanner));
        _local = localCache;
    }

    public void Register(string seed, Func<object[], Task<byte[]>> getData, TimeSpan? expiration = null,
        TimeSpan? localExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(getData);

        var carrier = new Carrier
        {
            Expiration = expiration ?? _defaultCacheExpiration,
            LocalExpiration = localExpiration ?? TimeSpan.Zero,
            GetDataAsync = getData
        };

        if (!_providers.TryAdd(seed, carrier))
            throw new ArgumentException($"Cannot add duplicate key '{seed}' in cache provider dictionary");

        _logger.LogDebug("Registered cache provider for seed: {Seed} with expiration: {Expiration}",
            seed, expiration ?? _defaultCacheExpiration);
    }

    public void Unregister(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _providers.TryRemove(seed, out _);
        _logger.LogDebug("Unregistered cache provider for seed: {Seed}", seed);
    }

    private static string MakeKey(string seed, string category, object[]? parameters)
    {
        return parameters == null
            ? $":{seed}:{category}"
            : $":{seed}:{category}:{string.Join("-", parameters.Where(p => p is not Delegate).Select(p => p == null ? "null" : p.ToString()))}";
    }

    public async Task RemoveAsync(string seed, string category, params object[] parameters)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(category);

        var key = MakeKey(seed, category, parameters);
        if (_inFlight.TryRemove(key, out var flight))
            flight.Invalidate();
        _local?.Remove(key);

        try
        {
            await _cache.RemoveAsync(key);
            _logger.LogDebug("Removed cache entry with key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove cache entry with key: {Key}", key);
            throw;
        }
    }

    public async Task RemoveByCategoryAsync(string seed, string category)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(category);

        // Build pattern using the same key format: ":seed:category:*"
        if (_localCategories.TryRemove(MakeKey(seed, category, null), out var local))
        {
            local.Cancel();
            local.Dispose();
        }
        var prefix = MakeKey(seed, category, null) + ":";
        foreach (var entry in _inFlight)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                _inFlight.TryRemove(entry))
                entry.Value.Invalidate();
        }

        var pattern = prefix + "*";
        var deletedCount = await _keyScanner.RemoveByPatternAsync(pattern);

        _logger.LogInformation(
            "Removed {Count} cache entries for {Seed}:{Category}",
            deletedCount, seed, category);
    }

    public bool IsRegistered(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return _providers.ContainsKey(seed);
    }

    public async Task<byte[]> GetAsync(string seed, string category, params object[] parameters)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(category);

        if (!_providers.TryGetValue(seed, out var carrier))
            throw new ArgumentException($"No provider registered for seed: {seed}");

        var key = MakeKey(seed, category, parameters);

        var useLocal = _local != null && carrier.LocalExpiration > TimeSpan.Zero;
        if (useLocal && _local!.TryGetValue(key, out byte[]? local) && local != null)
            return local;

        // Taken before the read: an invalidation that runs meanwhile cancels it, so data read
        // before the invalidation is never kept in memory
        var token = useLocal ? LocalToken(seed, category) : CancellationToken.None;

        var data = await TryGetCachedAsync(key);
        if (data == null)
        {
            var flight = new Flight();
            var current = _inFlight.GetOrAdd(key, flight);
            if (ReferenceEquals(current, flight))
                await RunFlightAsync(key, carrier, parameters, flight);
            else
                _logger.LogDebug("Joining in-flight generation for key: {Key}", key);

            data = await current.Completion;
        }

        if (useLocal && !token.IsCancellationRequested)
            _local!.Set(key, data, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = carrier.LocalExpiration,
                ExpirationTokens = { new CancellationChangeToken(token) }
            });

        return data;
    }

    private CancellationToken LocalToken(string seed, string category) =>
        _localCategories.GetOrAdd(MakeKey(seed, category, null), _ => new CancellationTokenSource()).Token;

    /// <summary>
    /// Generates the data for <paramref name="key"/> once and completes <paramref name="flight"/>
    /// with it (or with the generation's exception). Never throws.
    /// </summary>
    private async Task RunFlightAsync(string key, Carrier carrier, object[] parameters, Flight flight)
    {
        try
        {
            // A flight for this key that finished between our cache miss and our registration has
            // already stored its result: re-check so that a key is generated once per miss window.
            var data = await TryGetCachedAsync(key);
            if (data == null)
            {
                _logger.LogDebug("Cache miss for key: {Key}, generating data", key);
                data = await carrier.GetDataAsync(parameters);

                if (!flight.Invalidated)
                {
                    await TrySetCachedAsync(key, data, carrier.Expiration);

                    // Invalidation flags the flight before it removes the key, so an invalidation
                    // that raced with the write above is visible here: drop what we just stored.
                    if (flight.Invalidated)
                        await TryRemoveCachedAsync(key);
                }
                else
                {
                    _logger.LogDebug("Key {Key} was invalidated during generation; not caching the result", key);
                }
            }

            _inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));
            flight.Complete(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate data for key: {Key}", key);
            _inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));
            flight.Fail(ex);
        }
    }

    private async Task<byte[]?> TryGetCachedAsync(string key)
    {
        try
        {
            var cachedData = await _cache.GetAsync(key);
            if (cachedData != null)
                _logger.LogDebug("Cache hit for key: {Key}, size: {Size} bytes", key, cachedData.Length);
            return cachedData;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache read failed for key: {Key}; generating the data instead", key);
            return null;
        }
    }

    private async Task TrySetCachedAsync(string key, byte[] data, TimeSpan expiration)
    {
        try
        {
            await _cache.SetAsync(key, data, new DistributedCacheEntryOptions { SlidingExpiration = expiration });
            _logger.LogDebug("Data generated and cached for key: {Key}, size: {Size} bytes", key, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache write failed for key: {Key}; returning the generated data uncached", key);
        }
    }

    private async Task TryRemoveCachedAsync(string key)
    {
        try
        {
            await _cache.RemoveAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to drop invalidated cache entry with key: {Key}", key);
        }
    }

    public async Task SetAsync(string seed, string category, byte[] data, params object[] parameters)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(data);

        if (!_providers.TryGetValue(seed, out var carrier))
        {
            _logger.LogWarning("Attempted to set cache data for unregistered seed: {Seed}", seed);
            return;
        }

        var key = MakeKey(seed, category, parameters);
        _local?.Remove(key);

        try
        {
            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = carrier.Expiration
            };

            await _cache.SetAsync(key, data, options);
            _logger.LogDebug("Set cache data for key: {Key}, size: {Size} bytes", key, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set cache data for key: {Key}", key);
            throw;
        }
    }
}