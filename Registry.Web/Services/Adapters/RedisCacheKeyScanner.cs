using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using StackExchange.Redis;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Redis-based cache key scanner that uses SCAN + DELETE for pattern-based removal.
/// Handles the InstanceName prefix that RedisCache adds to keys.
/// </summary>
public class RedisCacheKeyScanner : ICacheKeyScanner
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _instanceName;
    private readonly ILogger<RedisCacheKeyScanner> _logger;

    public RedisCacheKeyScanner(IConnectionMultiplexer redis, string instanceName,
        ILogger<RedisCacheKeyScanner> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _instanceName = instanceName ?? "";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> RemoveByPatternAsync(string pattern)
    {
        // Prepend the instance name to match Redis key format
        // e.g., pattern ":thumb:org/ds:*" becomes "Registry:thumb:org/ds:*"
        var fullPattern = _instanceName + pattern;

        try
        {
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length == 0)
            {
                _logger.LogWarning("No Redis endpoints available for pattern-based cache removal");
                return 0;
            }

            var server = _redis.GetServer(endpoints[0]);
            var db = _redis.GetDatabase();
            var deletedCount = 0;

            await foreach (var key in server.KeysAsync(pattern: fullPattern))
            {
                await db.KeyDeleteAsync(key);
                deletedCount++;
            }

            _logger.LogDebug("Removed {Count} cache entries matching pattern '{Pattern}'",
                deletedCount, fullPattern);
            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove cache entries by pattern '{Pattern}'", fullPattern);
            return 0;
        }
    }
}
