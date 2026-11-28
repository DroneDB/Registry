using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Services.Objects;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The lock the copies and transfers of a dataset take: one holder per key, several keys in one fixed order,
/// nothing held after a cancelled wait. Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~KeyedAsyncLockTests"
/// </summary>
[TestFixture]
public class KeyedAsyncLockTests
{
    [Test]
    public async Task ASecondHolderOfTheSameKey_WaitsForTheRelease()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync(["org/ds"]);

        var second = locks.AcquireAsync(["org/ds"]);
        second.IsCompleted.ShouldBeFalse();

        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task AnotherKey_IsNotBlocked()
    {
        var locks = new KeyedAsyncLock();
        using var first = await locks.AcquireAsync(["org/a"]);

        using var other = await locks.AcquireAsync(["org/b"]).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // A transfer from A to B and one from B to A need the same two keys: taken in one order they never wait on each other
    [Test]
    public async Task TheSameKeysInOppositeOrders_NeverDeadlock()
    {
        var locks = new KeyedAsyncLock();
        var held = 0;
        var overlapped = false;

        async Task Run(string[] keys)
        {
            for (var i = 0; i < 200; i++)
            {
                using var lease = await locks.AcquireAsync(keys);
                if (Interlocked.Increment(ref held) > 1)
                    overlapped = true;
                await Task.Yield();
                Interlocked.Decrement(ref held);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => Task.Run(() => Run(i % 2 == 0 ? ["org/a", "org/b"] : ["org/b", "org/a"]))))
            .WaitAsync(TimeSpan.FromSeconds(30));

        overlapped.ShouldBeFalse();
    }

    [Test]
    public async Task ACancelledWait_HoldsNothing_AndLeavesTheKeyFree()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync(["org/b"]);
        using var cancel = new CancellationTokenSource();

        // Takes org/a, then waits for org/b held above
        var waiting = locks.AcquireAsync(["org/a", "org/b"], cancel.Token);
        waiting.IsCompleted.ShouldBeFalse();
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);

        // org/a was released with the cancelled wait
        using (await locks.AcquireAsync(["org/a"]).WaitAsync(TimeSpan.FromSeconds(10))) { }
        first.Dispose();
        using (await locks.AcquireAsync(["org/b"]).WaitAsync(TimeSpan.FromSeconds(10))) { }
    }

    [Test]
    public async Task ARepeatedKey_IsTakenOnce()
    {
        var locks = new KeyedAsyncLock();

        using var lease = await locks.AcquireAsync(["org/a", "org/a"]).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
