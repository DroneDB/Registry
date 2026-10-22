#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// REG-07: concurrent misses for one key share a single generation, cache I/O failures fall back
/// to generating the data, and invalidation never lets a pre-invalidation generation be reused
/// or stored.
/// </summary>
[TestFixture]
public class CacheManagerSingleFlightTests
{
    private const string Seed = "tiles";
    private const string Category = "ds";
    private static readonly byte[] Payload = [1, 2, 3];

    /// <summary>In-memory cache whose operations can be made to fail or to run a hook.</summary>
    private sealed class FakeCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner =
            new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

        public bool FailGet { get; set; }
        public bool FailSet { get; set; }
        public Func<Task>? BeforeSet { get; set; }
        public int Sets { get; private set; }

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            FailGet ? throw new InvalidOperationException("cache down") : _inner.GetAsync(key, token);

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            if (FailSet) throw new InvalidOperationException("cache down");
            if (BeforeSet != null) await BeforeSet();
            Sets++;
            await _inner.SetAsync(key, value, options, token);
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

        public Task<byte[]?> PeekAsync(string key) => _inner.GetAsync(key);
    }

    private FakeCache _store = null!;
    private CacheManager _sut = null!;
    private int _invocations;

    [SetUp]
    public void SetUp()
    {
        _store = new FakeCache();
        _sut = new CacheManager(_store, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        _invocations = 0;
    }

    private static string Key(params object[] parameters) =>
        $":{Seed}:{Category}:{string.Join("-", parameters)}";

    /// <summary>Registers a provider that blocks until <paramref name="gate"/> completes.</summary>
    private void RegisterGated(Task<byte[]> gate) =>
        _sut.Register(Seed, async _ =>
        {
            Interlocked.Increment(ref _invocations);
            return await gate;
        });

    [Test]
    public async Task GetAsync_ConcurrentMisses_GenerateOnce()
    {
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterGated(gate.Task);

        // The fake cache completes synchronously, so each call has joined the flight (or started
        // it) by the time it returns its pending task: no timing assumptions.
        var callers = Enumerable.Range(0, 10).Select(_ => _sut.GetAsync(Seed, Category, "hash", 1, 2, 19)).ToArray();
        callers.ShouldAllBe(t => !t.IsCompleted);

        gate.SetResult(Payload);
        var results = await Task.WhenAll(callers);

        _invocations.ShouldBe(1);
        results.ShouldAllBe(r => r.SequenceEqual(Payload));
        _store.Sets.ShouldBe(1);
    }

    [Test]
    public async Task GetAsync_ConcurrentMissesOnThreadPool_GenerateOncePerKey()
    {
        // Generations hold until every caller has joined a flight: none can finish early and let a
        // late caller start a second generation of its key.
        const int count = 100;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.Register(Seed, async p =>
        {
            Interlocked.Increment(ref _invocations);
            await gate.Task;
            return [(byte)(int)p[0]];
        });

        // 4 keys x 25 callers racing from the thread pool. GetAsync has joined (or started) the
        // flight of its key by the time it returns its pending task (the fake cache is synchronous).
        var joined = 0;
        var allJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, count)
            .Select(i => Task.Run(() =>
            {
                var call = _sut.GetAsync(Seed, Category, i % 4);
                if (Interlocked.Increment(ref joined) == count) allJoined.SetResult();
                return call;
            }))
            .ToArray();
        await allJoined.Task;
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        _invocations.ShouldBe(4);
        for (var i = 0; i < results.Length; i++)
            results[i].ShouldBe([(byte)(i % 4)]);
    }

    [Test]
    public async Task GetAsync_AfterFlightCompletes_ServesFromCache()
    {
        _sut.Register(Seed, _ =>
        {
            Interlocked.Increment(ref _invocations);
            return Task.FromResult(Payload);
        });

        await _sut.GetAsync(Seed, Category, "a");
        await _sut.GetAsync(Seed, Category, "a");

        _invocations.ShouldBe(1);
    }

    [Test]
    public async Task GetAsync_CacheGetThrows_StillReturnsGeneratedData()
    {
        _store.FailGet = true;
        _sut.Register(Seed, _ => Task.FromResult(Payload));

        (await _sut.GetAsync(Seed, Category, "a")).ShouldBe(Payload);
    }

    [Test]
    public async Task GetAsync_CacheSetThrows_StillReturnsGeneratedData()
    {
        _store.FailSet = true;
        _sut.Register(Seed, _ => Task.FromResult(Payload));

        (await _sut.GetAsync(Seed, Category, "a")).ShouldBe(Payload);
    }

    [Test]
    public async Task GetAsync_GenerationThrows_AllWaitersFail_NextCallRetries()
    {
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterGated(gate.Task);

        var callers = Enumerable.Range(0, 5).Select(_ => _sut.GetAsync(Seed, Category, "a")).ToArray();
        gate.SetException(new InvalidOperationException("render failed"));

        foreach (var caller in callers)
            (await Should.ThrowAsync<InvalidOperationException>(caller)).Message.ShouldBe("render failed");
        _invocations.ShouldBe(1);

        // The failed flight is gone: the next call generates again.
        _sut.Unregister(Seed);
        _sut.Register(Seed, _ => Task.FromResult(Payload));
        (await _sut.GetAsync(Seed, Category, "a")).ShouldBe(Payload);
    }

    [Test]
    public async Task RemoveAsync_DuringGeneration_NewCallerDoesNotJoin_AndStaleResultIsNotCached()
    {
        var stale = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gates = new[] { stale.Task, fresh.Task };
        // Generations start on the pool (RQ-13): the second one is awaited, not counted synchronously
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.Register(Seed, async _ =>
        {
            var generation = Interlocked.Increment(ref _invocations);
            if (generation == 2) secondStarted.SetResult();
            return await gates[generation - 1];
        });

        var before = _sut.GetAsync(Seed, Category, "a");
        await _sut.RemoveAsync(Seed, Category, "a");
        var after = _sut.GetAsync(Seed, Category, "a");

        // A caller arriving after the invalidation must start a new generation (the stale one is still running)
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _invocations.ShouldBe(2);

        stale.SetResult([9]);
        (await before).ShouldBe([9]);
        (await _store.PeekAsync(Key("a"))).ShouldBeNull("the pre-invalidation result must not be cached");

        fresh.SetResult(Payload);
        (await after).ShouldBe(Payload);
        (await _store.PeekAsync(Key("a"))).ShouldBe(Payload);
    }

    [Test]
    public async Task RemoveByCategoryAsync_DuringGeneration_DetachesFlight()
    {
        var stale = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterGated(stale.Task);

        var before = _sut.GetAsync(Seed, Category, "a");
        await _sut.RemoveByCategoryAsync(Seed, Category);

        stale.SetResult([9]);
        await before;
        (await _store.PeekAsync(Key("a"))).ShouldBeNull();
    }

    [Test]
    public async Task RemoveAsync_RacingWithTheCacheWrite_DoesNotLeaveStaleData()
    {
        _sut.Register(Seed, _ => Task.FromResult(Payload));
        // The invalidation lands after the flight checked its flag but before its write.
        _store.BeforeSet = async () =>
        {
            _store.BeforeSet = null;
            await _sut.RemoveAsync(Seed, Category, "a");
        };

        (await _sut.GetAsync(Seed, Category, "a")).ShouldBe(Payload);
        (await _store.PeekAsync(Key("a"))).ShouldBeNull();
    }

    // REG-05: seeds with a local expiration keep their entries in memory; invalidation drops them
    [Test]
    public async Task LocalTier_ServesFromMemory_UntilInvalidated()
    {
        var sut = new CacheManager(_store, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance), new MemoryCache(new MemoryCacheOptions()));
        var version = 1;
        sut.Register(Seed, _ => Task.FromResult(new[] { (byte)version }), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        (await sut.GetAsync(Seed, Category, "a")).ShouldBe([(byte)1]);
        _store.FailGet = true; // memory only from here on
        (await sut.GetAsync(Seed, Category, "a")).ShouldBe([(byte)1]);
        _store.FailGet = false;

        version = 2;
        await sut.RemoveAsync(Seed, Category, "a");
        (await sut.GetAsync(Seed, Category, "a")).ShouldBe([(byte)2]);

        version = 3;
        await sut.RemoveByCategoryAsync(Seed, Category);
        await _store.RemoveAsync(Key("a")); // the in-memory test store has no pattern removal
        (await sut.GetAsync(Seed, Category, "a")).ShouldBe([(byte)3]);
    }
}
