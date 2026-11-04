using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Caching;

/// <summary>Default <see cref="IRecordCache"/> over the distributed cache (process-wide, single-flight per key).</summary>
public sealed class RecordCache : IRecordCache
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<RecordCache> _logger;

    // What a flight hands to every caller of the key: the value as computed or read
    private readonly SingleFlight<object?> _flights = new();

    public RecordCache(IDistributedCache cache, ILogger<RecordCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T> GetOrCreateRecordAsync<T>(string key, Func<CancellationToken, Task<T>> create,
        RecordCacheOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(options);

        var value = await GetOrCreateAsync(key, options, bytes => TryDeserialize<T>(key, bytes),
            async token =>
            {
                var computed = await create(token);
                return (computed, computed is null ? null : Encoding.UTF8.GetBytes(ApiJson.Serialize(computed)));
            }, ct);

        return value is T typed ? typed : default!;
    }

    public async Task<byte[]> GetOrCreateBytesAsync(string key, Func<CancellationToken, Task<byte[]>> create,
        RecordCacheOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(options);

        var value = await GetOrCreateAsync(key, options, bytes => (true, bytes),
            async token =>
            {
                var computed = await create(token);
                return (computed, computed);
            }, ct);

        return (byte[])value!;
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _flights.Invalidate(key);
        await FailOpen.RunAsync(() => _cache.RemoveAsync(key, ct), _logger, "remove", key, ct);
    }

    /// <summary>
    /// A read; on a miss (or a record that cannot be read) one computation shared by the concurrent callers,
    /// which reads again first (a flight that ended between the miss and this one stored its value), then
    /// computes and stores the value unless the cache condition refuses it or the key was invalidated meanwhile.
    /// </summary>
    private async Task<object?> GetOrCreateAsync(string key, RecordCacheOptions options,
        Func<byte[], (bool Ok, object? Value)> read, Func<CancellationToken, Task<(object? Value, byte[]? Bytes)>> compute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (await TryReadAsync(key, read, ct) is { Ok: true } hit)
            return hit.Value;

        return await _flights.RunAsync(key, async flight =>
        {
            // The flight's token: canceled once no caller waits for the record any more
            if (await TryReadAsync(key, read, flight.Token) is { Ok: true } again)
                return again.Value;

            var (value, bytes) = await compute(flight.Token);
            if (bytes != null && value != null && options.CacheWhen?.Invoke(value) != false && !flight.Invalidated)
            {
                await TrySetAsync(key, bytes, options);

                // An invalidation that raced with the write marked the flight first: drop what was stored
                if (flight.Invalidated)
                    await RemoveAsync(key);
            }

            return value;
        }, ct);
    }

    private async Task<(bool Ok, object? Value)> TryReadAsync(string key, Func<byte[], (bool Ok, object? Value)> read,
        CancellationToken ct)
    {
        // A read that fails is a miss: the record is computed instead
        var bytes = await FailOpen.RunAsync(() => _cache.GetAsync(key, ct), null, _logger, "read", key, ct);
        return bytes == null ? (false, null) : read(bytes);
    }

    // A write that fails leaves the record uncached; the computed value is still returned
    private Task TrySetAsync(string key, byte[] bytes, RecordCacheOptions options) =>
        FailOpen.RunAsync(() => _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.AbsoluteExpiration,
            SlidingExpiration = options.SlidingExpiration
        }), _logger, "write", key);

    // A record written by another version of the type is a miss, not this caller's error
    private (bool Ok, object? Value) TryDeserialize<T>(string key, byte[] bytes)
    {
        try
        {
            var value = ApiJson.Deserialize<T>(Encoding.UTF8.GetString(bytes));
            return value is null ? (false, null) : (true, value);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The cached record {Key} cannot be read; computing it again", key);
            return (false, null);
        }
    }
}
