using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Process-wide store of <see cref="UserAuthSnapshot"/>s (singleton). A snapshot is kept for
/// <see cref="CachingOptions.AuthorizationCacheExpiration"/>; <see cref="Invalidate"/> drops it
/// at once. Each invalidation bumps a per-user generation; a snapshot is stored with the
/// generation it was loaded at and served only while that generation is current, so a load
/// that read the identity store before a role change cannot put the old roles back, even when
/// the invalidation lands between its check and its write.
/// </summary>
public class UserAuthSnapshotCache
{
    public static readonly TimeSpan DefaultExpiration = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly TimeSpan _expiration;
    private readonly ConcurrentDictionary<string, long> _generations = new();

    public UserAuthSnapshotCache(IMemoryCache cache, IOptions<CachingOptions> settings)
        : this(cache, settings.Value.AuthorizationCacheExpiration ?? DefaultExpiration)
    {
    }

    public UserAuthSnapshotCache(IMemoryCache cache, TimeSpan expiration)
    {
        _cache = cache;
        _expiration = expiration;
    }

    private static string Key(string userId) => "user-auth-snapshot:" + userId;

    // A snapshot and the generation of the user it was loaded at
    private sealed record Entry(UserAuthSnapshot Snapshot, long Generation);

    public async Task<UserAuthSnapshot> GetOrLoadAsync(string userId, Func<Task<UserAuthSnapshot>> load)
    {
        if (_expiration <= TimeSpan.Zero)
            return await load();

        var generation = GenerationOf(userId);
        if (_cache.TryGetValue(Key(userId), out Entry? cached) && cached != null && cached.Generation == generation)
            return cached.Snapshot;

        var snapshot = await load();

        // A load that an invalidation overtook stores nothing; one that the invalidation overtakes between this
        // check and the write stores its old generation, which no read serves
        if (GenerationOf(userId) == generation)
            _cache.Set(Key(userId), new Entry(snapshot, generation), _expiration);

        return snapshot;
    }

    /// <summary>Changes every time the user's snapshot is invalidated.</summary>
    public long GenerationOf(string userId) => _generations.GetValueOrDefault(userId);

    public void Invalidate(string userId)
    {
        _generations.AddOrUpdate(userId, 1, (_, g) => g + 1);
        _cache.Remove(Key(userId));
    }
}
