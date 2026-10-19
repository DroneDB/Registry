#nullable enable
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
/// <see cref="AppSettings.AuthorizationCacheExpiration"/>; <see cref="Invalidate"/> drops it
/// at once. Each invalidation bumps a per-user generation, and a load only stores its result
/// if the generation did not change while it ran, so a load that read the identity store
/// before a role change cannot put the old roles back after the invalidation.
/// </summary>
public class UserAuthSnapshotCache
{
    public static readonly TimeSpan DefaultExpiration = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly TimeSpan _expiration;
    private readonly ConcurrentDictionary<string, long> _generations = new();

    public UserAuthSnapshotCache(IMemoryCache cache, IOptions<AppSettings> settings)
        : this(cache, settings.Value.AuthorizationCacheExpiration ?? DefaultExpiration)
    {
    }

    public UserAuthSnapshotCache(IMemoryCache cache, TimeSpan expiration)
    {
        _cache = cache;
        _expiration = expiration;
    }

    private static string Key(string userId) => "user-auth-snapshot:" + userId;

    public async Task<UserAuthSnapshot> GetOrLoadAsync(string userId, Func<Task<UserAuthSnapshot>> load)
    {
        if (_expiration <= TimeSpan.Zero)
            return await load();

        if (_cache.TryGetValue(Key(userId), out UserAuthSnapshot? cached) && cached != null)
            return cached;

        var generation = GenerationOf(userId);
        var snapshot = await load();

        if (GenerationOf(userId) == generation)
            _cache.Set(Key(userId), snapshot, _expiration);

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
