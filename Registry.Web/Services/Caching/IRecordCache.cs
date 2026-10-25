#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Caching;

/// <summary>
/// Records in the distributed cache, computed on a miss: the one place where Registry writes the
/// get-then-set sequence. A miss is computed once per key within the process (single-flight), and the cache
/// never fails a request: a read or write error is logged and the computed value returned (fail-open).
/// </summary>
public interface IRecordCache
{
    /// <summary>
    /// The record stored at <paramref name="key"/>, or the value of <paramref name="create"/>, stored with
    /// <paramref name="options"/> unless it is null or <see cref="RecordCacheOptions.CacheWhen"/> refuses it.
    /// Records are JSON (Json.NET), as <c>DistributedCacheExtensions</c> wrote them.
    /// </summary>
    /// <param name="key">The cache key (<see cref="CacheCategories"/>).</param>
    /// <param name="create">Computes the value on a miss; shared by the concurrent callers of the key.</param>
    /// <param name="options">Expiry and the cache condition.</param>
    /// <param name="ct">Ends this caller's wait (the shared computation runs on).</param>
    Task<T> GetOrCreateRecordAsync<T>(string key, Func<CancellationToken, Task<T>> create, RecordCacheOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// The bytes stored at <paramref name="key"/>, or those of <paramref name="create"/>: the same rules for a
    /// caller that serializes its records itself (it keys an ETag on the stored bytes).
    /// </summary>
    Task<byte[]> GetOrCreateBytesAsync(string key, Func<CancellationToken, Task<byte[]>> create,
        RecordCacheOptions options, CancellationToken ct = default);

    /// <summary>Removes the record; a computation of it in progress is not stored.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);
}

/// <summary>How long a record is kept, and whether a computed value is kept at all.</summary>
/// <param name="AbsoluteExpiration">Kept at most this long after it was stored.</param>
/// <param name="SlidingExpiration">Dropped when not read for this long.</param>
public sealed record RecordCacheOptions(TimeSpan? AbsoluteExpiration = null, TimeSpan? SlidingExpiration = null)
{
    /// <summary>Whether a computed value is stored (null: every non-null value).</summary>
    public Func<object, bool>? CacheWhen { get; init; }

    /// <summary>Kept <paramref name="ttl"/> after it was stored.</summary>
    public static RecordCacheOptions For(TimeSpan ttl) => new(AbsoluteExpiration: ttl);
}
