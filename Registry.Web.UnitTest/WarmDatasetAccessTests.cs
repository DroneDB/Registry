#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Auth;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// REG-05: a warm request to a dataset (each COPC range request of a point cloud) checks access
/// without a Redis round trip or a database query: the visibility stays in process memory and the
/// organization's members come with the cached dataset lookup. Changes are seen at once.
/// </summary>
[TestFixture]
public class WarmDatasetAccessTests
{
    private const string Org = "acme";
    private const string Ds = "survey";
    private const string Owner = "owner";
    private const string Member = "member";
    private const string Stranger = "stranger";

    /// <summary>Distributed cache that counts reads.</summary>
    private sealed class CountingCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner =
            new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

        public int Reads;

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Interlocked.Increment(ref Reads);
            return _inner.GetAsync(key, token);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            _inner.SetAsync(key, value, options, token);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }

    /// <summary>Counts the rows EF materializes (each one comes from a query).</summary>
    private sealed class RowCounter : IMaterializationInterceptor
    {
        public int Rows;

        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            Interlocked.Increment(ref Rows);
            return entity;
        }
    }

    private sealed class Snapshots : IUserAuthSnapshotProvider
    {
        public Task<UserAuthSnapshot> GetAsync(string userId) =>
            Task.FromResult(new UserAuthSnapshot(true, userId, false, false));
    }

    private TestDb<RegistryContext> _db = null!;
    private DbContextOptions<RegistryContext> _options = null!;
    private DatasetLookupCache _lookups = null!;
    private CountingCache _redis = null!;
    private CacheManager _cache = null!;
    // One instance for the fixture (tests read deltas): a materialization interceptor is a singleton
    // interceptor, part of EF's internal service provider key, so a new instance per test would build
    // a new internal provider per test and exhaust EF's per-process limit (ManyServiceProvidersCreatedWarning)
    private readonly RowCounter _rows = new();
    private Visibility _visibility;

    // One instance: the visibility key includes it, as the singleton in the application
    private readonly IDdbManager _ddb = Mock.Of<IDdbManager>();

    [SetUp]
    public async Task SetUp()
    {
        _lookups = new DatasetLookupCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        _db = TestDb.Create<RegistryContext>(b => b.AddInterceptors(new DatasetChangeInterceptor(_lookups), _rows));
        _options = _db.Options;

        await using (var seed = new RegistryContext(_options))
        {
            var org = new Organization { Slug = Org, Name = "Acme", OwnerId = Owner, CreationDate = DateTime.UtcNow };
            seed.Organizations.Add(org);
            seed.Datasets.Add(new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), Organization = org, CreationDate = DateTime.UtcNow });
            seed.Set<OrganizationUser>().Add(new OrganizationUser
            {
                Organization = org, OrganizationSlug = Org, UserId = Member, Permissions = OrganizationPermissions.ReadOnly
            });
            await seed.SaveChangesAsync();
        }

        _visibility = Visibility.Private;
        _redis = new CountingCache();
        _cache = new CacheManager(_redis, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance), new MemoryCache(new MemoryCacheOptions()));
        _cache.Register(MagicStrings.DatasetVisibilityCacheSeed,
            _ => Task.FromResult(BitConverter.GetBytes((int)_visibility)), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
    }

    private async Task<bool> CanRead(RegistryContext context, string userId)
    {
        var utils = new WebUtils(Mock.Of<IAuthManager>(), context, Microsoft.Extensions.Options.Options.Create(new AccountOptions()),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions()),
            Mock.Of<IHttpContextAccessor>(), Mock.Of<IDatasetSummaryProvider>(), _lookups);
        var access = new DatasetAccessControl(new Snapshots(), context, NullLogger.Instance, _ddb, _cache);
        return await access.CanAccessDataset(await utils.GetDatasetAsync(Org, Ds), AccessType.Read, userId);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task WarmMemberRequest_MakesNoQueryAndNoRedisRead()
    {
        await using var context = new RegistryContext(_options);
        (await CanRead(context, Member)).ShouldBeTrue();
        (await CanRead(context, Stranger)).ShouldBeFalse();

        var rows = _rows.Rows;
        var reads = _redis.Reads;
        for (var i = 0; i < 20; i++)
        {
            (await CanRead(context, Member)).ShouldBeTrue();
            (await CanRead(context, Stranger)).ShouldBeFalse();
        }

        (_rows.Rows - rows).ShouldBe(0, "dataset and members come from the lookup cache");
        (_redis.Reads - reads).ShouldBe(0, "the visibility stays in process memory");
    }

    [Test]
    public async Task NewMember_IsSeenAtOnce()
    {
        await using (var context = new RegistryContext(_options))
            (await CanRead(context, Stranger)).ShouldBeFalse();

        await using (var write = new RegistryContext(_options))
        {
            write.Set<OrganizationUser>().Add(new OrganizationUser
            {
                Organization = await write.Organizations.FirstAsync(o => o.Slug == Org), OrganizationSlug = Org,
                UserId = Stranger, Permissions = OrganizationPermissions.ReadOnly
            });
            await write.SaveChangesAsync();
        }

        await using (var context = new RegistryContext(_options))
            (await CanRead(context, Stranger)).ShouldBeTrue();
    }

    [Test]
    public async Task RemovedMember_IsSeenAtOnce()
    {
        await using (var context = new RegistryContext(_options))
            (await CanRead(context, Member)).ShouldBeTrue();

        await using (var write = new RegistryContext(_options))
        {
            write.Set<OrganizationUser>().RemoveRange(write.Set<OrganizationUser>().Where(u => u.UserId == Member));
            await write.SaveChangesAsync();
        }

        await using (var context = new RegistryContext(_options))
            (await CanRead(context, Member)).ShouldBeFalse();
    }

    [Test]
    public async Task VisibilityChange_IsSeenAtOnce()
    {
        await using var context = new RegistryContext(_options);
        (await CanRead(context, Stranger)).ShouldBeFalse();

        // What DatasetsManager/MetaManager do when the visibility changes
        _visibility = Visibility.Public;
        var ds = await context.Datasets.AsNoTracking().FirstAsync();
        await _cache.RemoveAsync(MagicStrings.DatasetVisibilityCacheSeed, Org, Org, ds.InternalRef, _ddb);

        (await CanRead(context, Stranger)).ShouldBeTrue();
    }
}
