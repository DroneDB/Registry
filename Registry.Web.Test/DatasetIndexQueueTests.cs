using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common.Test;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class DatasetIndexQueueTests
{
    private static (DatasetIndexQueue Queue, Mock<IDDB> Ddb) CreateQueue(IndexQueueSettings? opts = null)
    {
        var ddb = new Mock<IDDB>();
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ddbManager.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            IndexQueue = opts ?? new IndexQueueSettings { BatchWindowMs = 20, MaxBatchSize = 64 }
        });

        var queue = new DatasetIndexQueue(scopeFactory, NullLogger<DatasetIndexQueue>.Instance, settings);
        return (queue, ddb);
    }

    private static Entry MakeEntry(string path) => new() { Path = path, Hash = "h_" + path, Type = EntryType.Generic };

    private static BatchAddedEntry MakeAdded(string path, string status = "added") =>
        new() { Path = path, Hash = "h_" + path, Type = EntryType.Generic, Status = status };

    [Test]
    public async Task EnqueueAsync_TwentyConcurrentCallsSameDataset_CoalescesIntoFewNativeBatchCalls()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());
        var callCount = 0;

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) =>
            {
                Interlocked.Increment(ref callCount);
                return new BatchAddResult
                {
                    Entries = paths.Select(p => MakeAdded(p)).ToList()
                };
            });

        var tasks = Enumerable.Range(0, 20)
            .Select(i => queue.EnqueueAsync(key, $"file{i}.txt"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Length.ShouldBe(20);
        // 20 requests batched with MaxBatchSize=64 and a shared coalescing window should produce
        // far fewer than 20 native calls - the core point of the coalescer.
        callCount.ShouldBeLessThan(20);
    }

    [Test]
    public async Task EnqueueAsync_EachCallerGetsOwnEntryMatchedByPath()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) => new BatchAddResult
            {
                Entries = paths.Select(p => MakeAdded(p)).ToList()
            });

        var t1 = queue.EnqueueAsync(key, "a.txt");
        var t2 = queue.EnqueueAsync(key, "b.txt");
        await Task.WhenAll(t1, t2);

        t1.Result.Path.ShouldBe("a.txt");
        t2.Result.Path.ShouldBe("b.txt");
    }

    [Test]
    public async Task EnqueueAsync_PerItemFailure_FailsOnlyThatCallersTask()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) => new BatchAddResult
            {
                Entries = paths.Where(p => p != "bad.txt").Select(p => MakeAdded(p)).ToList(),
                Errors = paths.Contains("bad.txt")
                    ? [new BatchAddItemError { Path = "bad.txt", Code = "FS", Message = "does not exist" }]
                    : []
            });

        var good = queue.EnqueueAsync(key, "good.txt");
        var bad = queue.EnqueueAsync(key, "bad.txt");

        var goodResult = await good;
        goodResult.Path.ShouldBe("good.txt");

        var ex = await Should.ThrowAsync<Exception>(async () => await bad);
        ex.Message.ShouldContain("does not exist");
    }

    [Test]
    public async Task EnqueueAsync_UnchangedPath_ResolvesEntryViaGetEntry()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns(new BatchAddResult
            {
                Unchanged = [new BatchAddUnchangedItem { Path = "same.txt" }]
            });
        ddb.Setup(d => d.GetEntry("same.txt")).Returns(MakeEntry("same.txt"));

        var result = await queue.EnqueueAsync(key, "same.txt");

        result.Path.ShouldBe("same.txt");
        ddb.Verify(d => d.GetEntry("same.txt"), Times.Once);
    }

    [Test]
    public async Task EnqueueAsync_BatchThrowingDdbBusyException_RetriesOnceThenSucceeds()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());
        var attempt = 0;

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                    throw new DdbBusyException("locked");
                return new BatchAddResult { Entries = paths.Select(p => MakeAdded(p)).ToList() };
            });

        var result = await queue.EnqueueAsync(key, "a.txt");

        result.Path.ShouldBe("a.txt");
        attempt.ShouldBe(2);
    }

    [Test]
    public async Task EnqueueAsync_BatchThrowingDdbBusyExceptionTwice_FailsWithTransientException()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Throws(new DdbBusyException("locked"));

        await Should.ThrowAsync<TransientException>(async () => await queue.EnqueueAsync(key, "a.txt"));
    }

    [Test]
    public async Task EnqueueAsync_ThrowingDrainIteration_DoesNotKillTheLane()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());
        var attempt = 0;

        // First call throws a non-Ddb exception (simulates a genuine bug/db-scoped failure);
        // the lane must keep processing subsequent enqueues rather than hanging forever.
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                    throw new InvalidOperationException("boom");
                return new BatchAddResult { Entries = paths.Select(p => MakeAdded(p)).ToList() };
            });

        await Should.ThrowAsync<Exception>(async () => await queue.EnqueueAsync(key, "first.txt"));

        // The lane must still be alive for a subsequent, independent enqueue.
        var result = await queue.EnqueueAsync(key, "second.txt");
        result.Path.ShouldBe("second.txt");
    }

    [Test]
    public async Task EnqueueAsync_CompletenessGap_FailsRatherThanHangs()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org", Guid.NewGuid());

        // Adapter bug simulation: native result omits the requested path from all 3 buckets.
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns(new BatchAddResult());

        var ex = await Should.ThrowAsync<Exception>(async () => await queue.EnqueueAsync(key, "ghost.txt"));
        ex.Message.ShouldContain("ghost.txt");
    }

    [Test]
    public void Constructor_InvalidSettings_FailsFast()
    {
        var exWindow = Assert.Throws<ArgumentException>(
            () => CreateQueue(new IndexQueueSettings { BatchWindowMs = 0 }));
        exWindow.Message.ShouldContain("BatchWindowMs");

        var exBatch = Assert.Throws<ArgumentException>(
            () => CreateQueue(new IndexQueueSettings { MaxBatchSize = 0 }));
        exBatch.Message.ShouldContain("MaxBatchSize");

        var exTimeout = Assert.Throws<ArgumentException>(
            () => CreateQueue(new IndexQueueSettings { EnqueueTimeoutSeconds = 0 }));
        exTimeout.Message.ShouldContain("EnqueueTimeoutSeconds");
    }

    [Test]
    public async Task EnqueueAsync_DrainIterationFails_ResolvesWithErrorInsteadOfHanging()
    {
        // IDdbManager resolution inside the drain iteration throws (simulates a scoped-
        // resolution failure escaping CommitBatchAsync's own try): the caller must observe
        // the failure, never await Task.WhenAll forever.
        var ddb = new Mock<IDDB>();
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Throws(new Exception("scope resolution failed"));

        var services = new ServiceCollection();
        services.AddSingleton(ddbManager.Object);
        var provider = services.BuildServiceProvider();

        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            IndexQueue = new IndexQueueSettings()
        });

        var queue = new DatasetIndexQueue(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatasetIndexQueue>.Instance, settings);

        var key = new DatasetKey("org", Guid.NewGuid());
        var task = queue.EnqueueAsync(key, "a.txt");

        // Throws TimeoutException on a hang (the regression this guards); the completion fault is
        // surfaced separately so we can assert on its message.
        var finished = await TestUtils.AwaitWithin(task, TimeSpan.FromSeconds(15));
        var ex = Should.Throw<Exception>(async () => await finished);
        ex.Message.ShouldContain("scope resolution failed");
    }

    /// <summary>
    /// <see cref="IDatasetIndexQueue.Release"/> (invoked on dataset cleanup) must not break
    /// subsequent writes: a following enqueue transparently recreates the lane and commits.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_AfterRelease_RecreatesLaneAndSucceeds()
    {
        var (queue, ddb) = CreateQueue();
        var key = new DatasetKey("org-release", Guid.NewGuid());

        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) => new BatchAddResult
            {
                Entries = paths.Select(p => MakeAdded(p)).ToList()
            });

        (await queue.EnqueueAsync(key, "a.txt")).Path.ShouldBe("a.txt");

        // simulate the dataset-cleanup release
        queue.Release(key);

        var result = await queue.EnqueueAsync(key, "b.txt");
        result.Path.ShouldBe("b.txt");
    }
}
