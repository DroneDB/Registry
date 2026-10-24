using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace Registry.Web.Utilities;

public static class DistributedCacheExtensions
{
    private static readonly TimeSpan DefaultExpireTime = new(60);

    public static async Task SetRecordAsync<T>(this IDistributedCache cache, string recordId, T data,
        TimeSpan? absoluteExpireTime = null, TimeSpan? unusedExpireTime = null)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = absoluteExpireTime ?? DefaultExpireTime,
            SlidingExpiration = unusedExpireTime
        };

        var jsonData = ApiJson.Serialize(data);
        await cache.SetStringAsync(recordId, jsonData, options);
    }

    public static async Task<T> GetRecordAsync<T>(this IDistributedCache cache, string recordId)
    {
        var jsonData = await cache.GetStringAsync(recordId);
        return jsonData is null ? default : ApiJson.Deserialize<T>(jsonData);
    }
}