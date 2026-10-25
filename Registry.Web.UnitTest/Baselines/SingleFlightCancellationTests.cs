#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// A shared computation (single-flight: renders through <see cref="CacheManager"/>, records through the
/// record cache) runs for the callers waiting for it. A caller's token ends that caller's wait only; the
/// computation's token is canceled when the last waiting caller has cancelled, never while one still waits,
/// and an abandoned computation is never joined. Before, the computation took no token at all ("runs to
/// completion for every caller") and the leader waited for it inline, so its own cancellation could not end
/// its wait. Latches only: no delays, no timing assertions.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SingleFlightCancellationTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class SingleFlightCancellationTests
{
    private const string Key = "render";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>A computation that waits for its release and records what happened to its token.</summary>
    private sealed class Computation
    {
        private readonly TaskCompletionSource<int> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Starts;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public async Task<int> RunAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Starts);
            Token = token;
            token.Register(() => Canceled.TrySetResult());
            Started.TrySetResult();
            return await _release.Task.WaitAsync(token);
        }

        public void Release(int value) => _release.TrySetResult(value);
    }

    [Test]
    public async Task OneWaiterCancels_TheComputationGoesOnForTheOthers()
    {
        var flights = new SingleFlight<int>();
        var computation = new Computation();
        using var leaving = new CancellationTokenSource();

        var leaver = flights.RunAsync(Key, f => computation.RunAsync(f.Token), leaving.Token);
        await computation.Started.Task.WaitAsync(Guard);
        var staying = flights.RunAsync(Key, f => computation.RunAsync(f.Token));

        await leaving.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => leaver);

        computation.Token.IsCancellationRequested.ShouldBeFalse("a caller still waits");
        computation.Release(42);
        (await staying.WaitAsync(Guard)).ShouldBe(42);
        computation.Starts.ShouldBe(1);
    }

    [Test]
    public async Task TheLastWaiterCancels_TheComputationIsCanceled()
    {
        var flights = new SingleFlight<int>();
        var computation = new Computation();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var a = flights.RunAsync(Key, f => computation.RunAsync(f.Token), first.Token);
        await computation.Started.Task.WaitAsync(Guard);
        var b = flights.RunAsync(Key, f => computation.RunAsync(f.Token), second.Token);

        await first.CancelAsync();
        computation.Token.IsCancellationRequested.ShouldBeFalse();

        await second.CancelAsync();
        await computation.Canceled.Task.WaitAsync(Guard);
        await Should.ThrowAsync<OperationCanceledException>(() => a);
        await Should.ThrowAsync<OperationCanceledException>(() => b);
    }

    [Test]
    public async Task AnAbandonedComputation_IsNotJoined_ANewCallerStartsAFreshOne()
    {
        var flights = new SingleFlight<int>();
        var abandoned = new Computation();
        var fresh = new Computation();
        using var gone = new CancellationTokenSource();

        var first = flights.RunAsync(Key, f => abandoned.RunAsync(f.Token), gone.Token);
        await abandoned.Started.Task.WaitAsync(Guard);
        await gone.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        var next = flights.RunAsync(Key, f => fresh.RunAsync(f.Token));
        await fresh.Started.Task.WaitAsync(Guard);
        fresh.Release(7);

        (await next.WaitAsync(Guard)).ShouldBe(7);
        abandoned.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task TheLeadersCancellation_EndsItsWait_EvenIfTheComputationIgnoresTokens()
    {
        var flights = new SingleFlight<int>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leader = new CancellationTokenSource();
        await leader.CancelAsync();

        // A computation that never looks at a token and never finishes by itself
        await Should.ThrowAsync<OperationCanceledException>(() =>
            flights.RunAsync(Key, _ => release.Task, leader.Token).WaitAsync(Guard));
        release.SetResult(0);
    }

    [Test]
    public async Task ACallerWithoutAToken_KeepsTheComputationAlive()
    {
        var flights = new SingleFlight<int>();
        var computation = new Computation();
        using var leaving = new CancellationTokenSource();

        var keeper = flights.RunAsync(Key, f => computation.RunAsync(f.Token));
        await computation.Started.Task.WaitAsync(Guard);
        var leaver = flights.RunAsync(Key, f => computation.RunAsync(f.Token), leaving.Token);
        await leaving.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => leaver);

        computation.Token.IsCancellationRequested.ShouldBeFalse();
        computation.Release(1);
        (await keeper.WaitAsync(Guard)).ShouldBe(1);
    }

    [Test]
    public async Task CacheManager_TheGeneratorGetsTheFlightsToken_CanceledWhenTheRequestGoes()
    {
        var cache = new CacheManager(new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<CacheManager>.Instance, new NoopKeyScanner());
        cache.Register("tiles", Web.Utilities.CacheProviderFactories.CreateTileProvider());
        var computation = new Computation();
        using var request = new CancellationTokenSource();

        var tile = cache.GetAsync("tiles", "ds", request.Token, "h1", 1, 2, 3,
            new Func<CancellationToken, Task<byte[]>>(async token => [(byte)await computation.RunAsync(token)]));
        await computation.Started.Task.WaitAsync(Guard);

        await request.CancelAsync();
        await computation.Canceled.Task.WaitAsync(Guard);
        await Should.ThrowAsync<OperationCanceledException>(() => tile);
    }

    [Test]
    public async Task RecordCache_TheComputationGetsTheFlightsToken()
    {
        var records = TestCaches.Records(new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));
        var computation = new Computation();
        using var request = new CancellationTokenSource();

        var record = records.GetOrCreateRecordAsync(Key, token => computation.RunAsync(token),
            RecordCacheOptions.For(TimeSpan.FromMinutes(1)), request.Token);
        await computation.Started.Task.WaitAsync(Guard);

        await request.CancelAsync();
        await computation.Canceled.Task.WaitAsync(Guard);
        await Should.ThrowAsync<OperationCanceledException>(() => record);
    }

    private sealed class NoopKeyScanner : ICacheKeyScanner
    {
        public Task<int> RemoveByPatternAsync(string pattern) => Task.FromResult(0);
    }
}
