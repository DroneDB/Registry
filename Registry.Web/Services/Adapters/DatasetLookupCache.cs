using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IDatasetLookupCache"/> in process memory (datasets with their organization and its
/// members), for
/// <see cref="CachingOptions.DatasetLookupCacheExpiration"/> (default 30 s; zero disables it). An
/// organization-wide invalidation cancels a change token shared by that organization's entries.
/// Changes made by another process (a processing node) are seen when the entry expires.
/// </summary>
public sealed class DatasetLookupCache : IDatasetLookupCache
{
    public static readonly TimeSpan DefaultExpiration = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly TimeSpan _expiration;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _organizations = new();

    public DatasetLookupCache(IMemoryCache cache, IOptions<CachingOptions> settings)
        : this(cache, settings.Value.DatasetLookupCacheExpiration ?? DefaultExpiration)
    {
    }

    public DatasetLookupCache(IMemoryCache cache, TimeSpan expiration)
    {
        _cache = cache;
        _expiration = expiration;
    }

    public async Task<Dataset?> GetOrLoadAsync(string orgSlug, string dsSlug,
        Func<CancellationToken, Task<Dataset?>> load, CancellationToken ct = default)
    {
        if (_expiration <= TimeSpan.Zero)
            return await load(ct);

        var key = Key(orgSlug, dsSlug);
        if (_cache.TryGetValue(key, out Dataset? cached) && cached != null)
            return Copy(cached);

        // Taken before the load: an invalidation that runs during the load cancels it, so the
        // possibly stale row is never served from the cache
        var organization = _organizations.GetOrAdd(orgSlug, _ => new CancellationTokenSource());
        var token = organization.Token;

        var dataset = await load(ct);
        if (dataset == null)
            return null;

        if (!token.IsCancellationRequested)
            _cache.Set(key, Copy(dataset), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _expiration,
                ExpirationTokens = { new CancellationChangeToken(token) }
            });

        return dataset;
    }

    public void InvalidateOrganization(string orgSlug)
    {
        if (_organizations.TryRemove(orgSlug, out var organization))
        {
            organization.Cancel();
            organization.Dispose();
        }
    }

    public void InvalidateAll()
    {
        foreach (var orgSlug in _organizations.Keys)
            InvalidateOrganization(orgSlug);
    }

    private static string Key(string orgSlug, string dsSlug) => CacheCategories.DatasetLookup(orgSlug, dsSlug);

    // Callers may change what they get (EF entities are mutable): never hand out the cached instance
    private static Dataset Copy(Dataset d) => new()
    {
        Id = d.Id,
        Slug = d.Slug,
        InternalRef = d.InternalRef,
        CreationDate = d.CreationDate,
        FileTypes = d.FileTypes == null ? null! : (string[])d.FileTypes.Clone(),
        Organization = d.Organization == null
            ? null!
            : new Organization
            {
                Slug = d.Organization.Slug,
                Name = d.Organization.Name,
                Description = d.Organization.Description,
                CreationDate = d.Organization.CreationDate,
                OwnerId = d.Organization.OwnerId,
                IsPublic = d.Organization.IsPublic,
                // Null when the members were not loaded (the access check then loads them)
                Users = d.Organization.Users?.Select(u => new OrganizationUser
                {
                    UserId = u.UserId,
                    OrganizationSlug = u.OrganizationSlug,
                    Permissions = u.Permissions,
                    GrantedAt = u.GrantedAt,
                    GrantedBy = u.GrantedBy
                }).ToList()!
            }
    };
}
