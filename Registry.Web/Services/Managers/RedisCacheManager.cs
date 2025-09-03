using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;

#nullable enable

namespace Registry.Web.Services.Managers;

/// <summary>
/// Redis-based implementation of ICacheManager that maintains compatibility with the existing API.
/// This implementation stores binary data directly in Redis and returns byte arrays directly.
/// </summary>
public class RedisCacheManager : ICacheManager
{
    private class Carrier
    {
        public required Func<object[], Task<byte[]>> GetDataAsync { get; set; }
        public TimeSpan Expiration { get; set; }
    }

    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheManager> _logger;
    private readonly TimeSpan _defaultCacheExpiration = new(0, 30, 0);
    private readonly DictionaryEx<string, Carrier> _providers = new();

    public RedisCacheManager(IDistributedCache cache, ILogger<RedisCacheManager> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Register(string seed, Func<object[], Task<byte[]>> getData, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(getData);

        _providers.Add(seed, new Carrier
        {
            Expiration = expiration ?? _defaultCacheExpiration,
            GetDataAsync = getData
        });

        _logger.LogDebug("Registered cache provider for seed: {Seed} with expiration: {Expiration}",
            seed, expiration ?? _defaultCacheExpiration);
    }

    public void Unregister(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _providers.Remove(seed);
        _logger.LogDebug("Unregistered cache provider for seed: {Seed}", seed);
    }

    public static string MakeKey(string seed, string category, object[]? parameters)
    {
        return parameters == null
            ? $"{seed}-{category}"
            : $"{seed}-{category}:{string.Join(",", parameters.Select(p => p.ToString()))}";
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

    public async Task<bool> IsCachedAsync(string seed, string category, params object[] parameters)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(category);

        var key = MakeKey(seed, category, parameters);

        try
        {
            // For Redis, we need to actually try to get the value to check if it exists
            // This is because IDistributedCache doesn't have a Contains method
            var result = await _cache.GetAsync(key);
            return result != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check cache for key: {Key}", key);
            return false;
        }
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

            // Generate the data using the provider asynchronously
            var data = await carrier.GetDataAsync(parameters);

            // Store in Redis with expiration
            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = carrier.Expiration
            };

            await _cache.SetAsync(key, data, options);

            _logger.LogDebug("Cached data for key: {Key}, size: {Size} bytes", key, data.Length);
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

    public async Task ClearAsync(string seed, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var keyPrefix = category != null ? MakeKey(seed, category, null) : seed;

        try
        {
            // Unfortunately, Redis doesn't have a native way to get all keys with a prefix
            // through IDistributedCache. This is a limitation we need to document.
            // For now, we log the attempt but can't actually clear by prefix.

            _logger.LogWarning("Clear operation requested for key prefix: {KeyPrefix}. " +
                "Redis-based cache doesn't support prefix-based clearing through IDistributedCache. " +
                "Individual keys must be removed explicitly.", keyPrefix);

            // Alternative: if we really need this functionality, we'd need to:
            // 1. Use StackExchange.Redis directly with SCAN command
            // 2. Maintain a key registry
            // 3. Use a different approach for cache invalidation

            await Task.CompletedTask; // Placeholder for future implementation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear cache for prefix: {KeyPrefix}", keyPrefix);
            throw;
        }
    }
}
