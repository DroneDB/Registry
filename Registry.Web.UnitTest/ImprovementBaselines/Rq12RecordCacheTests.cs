#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-12 item 7: the 13 raw distributed-cache get-then-set sites (STAC, WMS, WMTS, WFS, WCS, the WCS probe,
/// the OGC layer catalog, the tasks list) go through <see cref="IRecordCache.GetOrCreateRecordAsync{T}"/>: a
/// miss is computed once per key in the process (single-flight), and a cache error never fails the request
/// (fail-open). The site cases (the layer catalog) were red before: a burst of misses listed the dataset once
/// per request, and a cache that cannot be read failed the request.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq12RecordCacheTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq12RecordCacheTests
{
    private static readonly RecordCacheOptions FiveMinutes = RecordCacheOptions.For(TimeSpan.FromMinutes(5));

    [Test]
    public async Task ConcurrentMisses_ComputeOnce()
    {
        var cache = TestCaches.Records();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var computed = 0;

        var callers = Enumerable.Range(0, 16).Select(_ => cache.GetOrCreateRecordAsync("k", async _ =>
        {
            Interlocked.Increment(ref computed);
            await gate.Task;
            return "value";
        }, FiveMinutes)).ToArray();

        gate.SetResult();
        (await Task.WhenAll(callers)).ShouldAllBe(v => v == "value");
        computed.ShouldBe(1);

        // Stored: the next caller reads it
        (await cache.GetOrCreateRecordAsync("k", _ => Task.FromResult("other"), FiveMinutes)).ShouldBe("value");
    }

    [Test]
    public async Task CacheReadFails_ComputesAndReturns()
    {
        var cache = new RecordCache(new FailingCache(failReads: true), NullLogger<RecordCache>.Instance);

        (await cache.GetOrCreateRecordAsync("k", _ => Task.FromResult(42), FiveMinutes)).ShouldBe(42);
    }

    [Test]
    public async Task CacheWriteFails_ReturnsTheComputedValue()
    {
        var cache = new RecordCache(new FailingCache(failWrites: true), NullLogger<RecordCache>.Instance);

        (await cache.GetOrCreateRecordAsync("k", _ => Task.FromResult("v"), FiveMinutes)).ShouldBe("v");
    }

    [Test]
    public async Task CacheWhenRefuses_NotStored_ButEveryCallerOfTheFlightGetsTheValue()
    {
        var cache = TestCaches.Records();
        var options = FiveMinutes with { CacheWhen = v => (int)v > 0 };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 4).Select(_ => cache.GetOrCreateRecordAsync("k", async _ =>
        {
            await gate.Task;
            return -1;
        }, options)).ToArray();
        gate.SetResult();

        (await Task.WhenAll(callers)).ShouldAllBe(v => v == -1);
        (await cache.GetOrCreateRecordAsync("k", _ => Task.FromResult(7), options)).ShouldBe(7);
    }

    [Test]
    public async Task RemovedWhileComputing_TheStaleValueIsNotStored()
    {
        var cache = TestCaches.Records();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stale = cache.GetOrCreateRecordAsync("k", async _ =>
        {
            started.SetResult();
            await gate.Task;
            return "stale";
        }, FiveMinutes);
        await started.Task;

        await cache.RemoveAsync("k");
        gate.SetResult();

        (await stale).ShouldBe("stale");
        (await cache.GetOrCreateRecordAsync("k", _ => Task.FromResult("fresh"), FiveMinutes)).ShouldBe("fresh");
    }

    [Test]
    public async Task ComputationFails_EveryCallerFails_TheNextCallComputesAgain()
    {
        var cache = TestCaches.Records();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cache.GetOrCreateRecordAsync<string>("k", _ => throw new InvalidOperationException("boom"), FiveMinutes));
        (await cache.GetOrCreateRecordAsync("k", _ => Task.FromResult("v"), FiveMinutes)).ShouldBe("v");
    }

    // ---- A site: the OGC layer catalog (red before the change) -------------------------------------

    [Test]
    public async Task LayerCatalog_ConcurrentMisses_ListTheDatasetOnce()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (catalog, ddb) = Catalog(TestCaches.MemoryStore());
        var finds = 0;
        ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns(() =>
        {
            Interlocked.Increment(ref finds);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5)); // bounded: a caller that does not join blocks here
            return [new Entry { Path = "ortho.tif", Hash = "h", Type = EntryType.GeoRaster }];
        });

        // The first caller lists the dataset; the others ask while it does (their misses are synchronous up to
        // the point where they join the listing in progress)
        var first = Task.Run(() => catalog.GetLayersAsync("org", "ds"));
        entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        var others = Enumerable.Range(0, 7).Select(_ => catalog.GetLayersAsync("org", "ds")).ToArray();
        release.Set();

        (await Task.WhenAll([first, .. others])).ShouldAllBe(l => l.Count == 1);
        finds.ShouldBe(1);
    }

    [Test]
    public async Task LayerCatalog_CacheCannotBeRead_StillListsTheLayers()
    {
        var (catalog, ddb) = Catalog(new FailingCache(failReads: true, failWrites: true));
        ddb.Setup(d => d.Find(It.IsAny<EntryQuery>()))
            .Returns([new Entry { Path = "ortho.tif", Hash = "h", Type = EntryType.GeoRaster }]);

        (await catalog.GetLayersAsync("org", "ds")).Single().Name.ShouldBe("ortho.tif");
    }

    private static (OgcLayerCatalog Catalog, Mock<IDDB> Ddb) Catalog(IDistributedCache store)
    {
        var dataset = new Dataset { InternalRef = Guid.NewGuid() };
        var utils = new Mock<IUtils>();
        utils.Setup(u => u.GetDatasetAsync("org", "ds", It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var ddb = new Mock<IDDB>();
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get("org", dataset.InternalRef)).Returns(ddb.Object);

        var catalog = new OgcLayerCatalog(ddbManager.Object, utils.Object,
            new RecordCache(store, NullLogger<RecordCache>.Instance), Mock.Of<IOgcBuildArtifactPaths>(),
            Mock.Of<IDdbWrapper>(), Mock.Of<IOgcCacheKeys>(k => k.ForAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>()) == Task.FromResult("layers-key")),
            NullLogger<OgcLayerCatalog>.Instance);
        return (catalog, ddb);
    }

    /// <summary>A distributed cache whose reads and/or writes fail (Redis down).</summary>
    private sealed class FailingCache(bool failReads = false, bool failWrites = false) : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new();

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            failReads
                ? throw new InvalidOperationException("It was not possible to connect to the redis server")
                : Task.FromResult(_entries.GetValueOrDefault(key));

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            if (failWrites)
                throw new InvalidOperationException("It was not possible to connect to the redis server");
            _entries[key] = value;
            return Task.CompletedTask;
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }
}
