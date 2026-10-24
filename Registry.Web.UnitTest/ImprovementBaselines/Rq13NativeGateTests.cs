#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-13 (XI-07): <see cref="INativeWorkGate"/> passes the caller's token into the work it runs: it is the
/// <see cref="DdbCancellation"/> of the work's native calls (the libddb adapter turns it into a cancel token),
/// linked with the token already current (a job's), and work whose caller went away while it waited for a slot
/// never starts. Before, the token canceled the wait only ("never running work").
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq13NativeGateTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq13NativeGateTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private readonly NativeWorkGate _gate = new(new NativeConcurrencySettings { Tiles = 1, Heavy = 1 });

    // Set when a test ends: a work item that waits for its token never keeps a pool thread, whatever failed
    private readonly ManualResetEventSlim _safety = new();

    [TearDown]
    public void ReleaseWork() => _safety.Set();

    [SetUp]
    public void ResetSafety() => _safety.Reset();

    public void Dispose()
    {
        _safety.Set();
        _safety.Dispose();
        _gate.Dispose();
    }

    // Stands for a native call that runs until libddb sees its cancel token canceled
    private void WaitForTheToken() => WaitHandle.WaitAny([DdbCancellation.Current.WaitHandle, _safety.WaitHandle]);

    [Test]
    public async Task Work_RunsWithTheCallersToken_AsItsNativeCancellation()
    {
        using var request = new CancellationTokenSource();

        var seen = await _gate.RunAsync(NativeLane.Tiles, () => DdbCancellation.Current, request.Token);
        var seenAsync = await _gate.RunAsync(NativeLane.Heavy, async () =>
        {
            await Task.Yield();
            return DdbCancellation.Current;
        }, request.Token);

        seen.ShouldBe(request.Token);
        seenAsync.ShouldBe(request.Token);
        DdbCancellation.Current.CanBeCanceled.ShouldBeFalse("the scope ends with the work");
    }

    [Test]
    public async Task ARequestCanceledWhileTheWorkRuns_IsSeenByIt()
    {
        using var request = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var work = _gate.RunAsync(NativeLane.Tiles, () =>
        {
            running.SetResult();
            WaitForTheToken();
            DdbCancellation.Current.ThrowIfCancellationRequested();
            return 0;
        }, request.Token);

        await running.Task.WaitAsync(Guard);
        await request.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => work.WaitAsync(Guard));
        _gate.InFlight(NativeLane.Tiles).ShouldBe(0);
    }

    [Test]
    public async Task InsideAJob_TheJobsTokenStillCancelsTheGatedWork()
    {
        using var job = new CancellationTokenSource();
        using var request = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> work;

        using (DdbCancellation.Enter(job.Token))
            work = _gate.RunAsync(NativeLane.Tiles, () =>
            {
                running.SetResult();
                WaitForTheToken();
                return job.IsCancellationRequested && !request.IsCancellationRequested;
            }, request.Token);

        await running.Task.WaitAsync(Guard);
        await job.CancelAsync();

        (await work.WaitAsync(Guard)).ShouldBeTrue("the work's token is linked with the job's");
    }

    [Test]
    public async Task ACallerThatWentAwayWhileWaiting_NeverStartsItsWork()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = _gate.RunAsync(NativeLane.Heavy, async () =>
        {
            occupied.SetResult();
            await release.Task;
            return 0;
        });
        await occupied.Task.WaitAsync(Guard);

        using var request = new CancellationTokenSource();
        var started = false;
        var waiting = _gate.RunAsync(NativeLane.Heavy, () => started = true, request.Token);
        await request.CancelAsync();
        release.SetResult();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting.WaitAsync(Guard));
        await busy.WaitAsync(Guard);
        started.ShouldBeFalse();
    }
}
