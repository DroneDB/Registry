using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Registry.Ports;

#nullable enable

namespace Registry.Web.Services.Managers;

public class CacheManager : ICacheManager
{
    private class Carrier
    {
        public required Func<object[], Task<byte[]>> GetDataAsync { get; init; }
        public TimeSpan Expiration { get; init; }
    }

    private readonly IDistributedCache _cache;
    private readonly ILogger<CacheManager> _logger;

    private readonly TimeSpan _defaultCacheExpiration = new(0, 30, 0);

    private readonly ConcurrentDictionary<string, Carrier> _providers = new();

    public CacheManager(IDistributedCache cache, ILogger<CacheManager> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Register(string seed, Func<object[], Task<byte[]>> getData, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(getData);

        var carrier = new Carrier
        {
            Expiration = expiration ?? _defaultCacheExpiration,
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

        try
        {
            // Try to get from cache first
            var cachedData = await _cache.GetAsync(key);
            if (cachedData != null)
            {
                _logger.LogDebug("Cache hit for key: {Key}, size: {Size} bytes", key, cachedData.Length);
                return cachedData;
            }

            _logger.LogDebug("Cache miss for key: {Key}, generating data", key);

            // Cache miss - generate data asynchronously
            var data = await carrier.GetDataAsync(parameters);

            // Store in cache with expiration
            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = carrier.Expiration
            };

            await _cache.SetAsync(key, data, options);

            _logger.LogDebug("Data generated and cached for key: {Key}, size: {Size} bytes", key, data.Length);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get/generate data for key: {Key}", key);
            throw;
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