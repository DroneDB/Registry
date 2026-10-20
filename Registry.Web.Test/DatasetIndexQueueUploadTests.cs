using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Shouldly;
using static Registry.Web.Test.DatasetIndexQueueTests;

namespace Registry.Web.Test;

/// <summary>
/// UPL-01 C.5 and E.1 on the index queue: uploads registered with BeginUpload do not wait for the
/// batch window once nothing else is streaming, while a registered upload still streaming keeps the
/// window open for its files; hash hints reach the native batch; each file gets its own outcome.
/// </summary>
[TestFixture]
public class DatasetIndexQueueUploadTests
{
    // A window long enough that waiting for it is unmistakable
    private static readonly IndexQueueSettings LongWindow = new() { BatchWindowMs = 5000, MaxBatchSize = 64 };

    private static void AcceptAll(Mock<IDDB> ddb, List<IReadOnlyList<AddItem>> batches)
    {
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<AddItem>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<AddItem> items, bool _) =>
            {
                lock (batches) batches.Add(items.ToArray());
                return new BatchAddResult { Entries = items.Select(i => MakeAdded(i.Path)).ToList() };
            });
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) =>
            {
                lock (batches) batches.Add(paths.Select(p => new AddItem(p)).ToArray());
                return new BatchAddResult { Entries = paths.Select(p => MakeAdded(p)).ToList() };
            });
    }

    private static AddItem Hinted(string path) => new(path, new FileHashHint("sha-" + path, 1, DateTime.UtcNow));

    [Test]
    public async Task SingleRegisteredUpload_CommitsWithoutWaitingForTheWindow()
    {
        var (queue, ddb) = CreateQueue(LongWindow);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        var watch = Stopwatch.StartNew();
        using var ticket = queue.BeginUpload(key);
        var outcomes = await queue.EnqueueAsync(key, [Hinted("a.jpg")], ticket);

        watch.ElapsedMilliseconds.ShouldBeLessThan(LongWindow.BatchWindowMs / 2);
        outcomes.Single().Entry!.Path.ShouldBe("a.jpg");
        ticket.Ended.ShouldBeTrue();
    }

    [Test]
    public async Task UnregisteredCallers_KeepTheWindow()
    {
        var (queue, ddb) = CreateQueue(new IndexQueueSettings { BatchWindowMs = 300, MaxBatchSize = 64 });
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);

        var watch = Stopwatch.StartNew();
        await queue.EnqueueAsync(new DatasetKey("org", Guid.NewGuid()), "a.txt");

        // The legacy path cannot know whether more callers are coming: it still waits
        watch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(250);
    }

    [Test]
    public async Task UploadStillStreaming_KeepsTheBatchOpenUntilItEnqueues()
    {
        var (queue, ddb) = CreateQueue(LongWindow);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        using var first = queue.BeginUpload(key);
        using var second = queue.BeginUpload(key);

        var firstDone = queue.EnqueueAsync(key, [Hinted("a.jpg")], first);
        // The second upload is still streaming: the first one's file waits for it...
        (await Task.WhenAny(firstDone, Task.Delay(200))).ShouldNotBe(firstDone);

        // ...and both commit in one batch as soon as it enqueues
        var watch = Stopwatch.StartNew();
        var secondDone = queue.EnqueueAsync(key, [Hinted("b.jpg")], second);
        await Task.WhenAll(firstDone, secondDone);

        watch.ElapsedMilliseconds.ShouldBeLessThan(LongWindow.BatchWindowMs / 2);
        batches.Count.ShouldBe(1);
        batches[0].Select(i => i.Path).ShouldBe(["a.jpg", "b.jpg"], ignoreOrder: true);
    }

    [Test]
    public async Task UploadEndingWithoutFiles_ReleasesTheBatch()
    {
        var (queue, ddb) = CreateQueue(LongWindow);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        using var uploading = queue.BeginUpload(key);
        var failed = queue.BeginUpload(key);

        var done = queue.EnqueueAsync(key, [Hinted("a.jpg")], uploading);
        (await Task.WhenAny(done, Task.Delay(200))).ShouldNotBe(done);

        // The other request failed before enqueuing anything: disposing its ticket wakes the batch
        var watch = Stopwatch.StartNew();
        failed.Dispose();
        await done;
        watch.ElapsedMilliseconds.ShouldBeLessThan(LongWindow.BatchWindowMs / 2);
    }

    [Test]
    public async Task Hints_ReachTheNativeBatch_AndEachFileGetsItsOutcome()
    {
        var (queue, ddb) = CreateQueue();
        IReadOnlyList<AddItem>? seen = null;
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<AddItem>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<AddItem> items, bool _) =>
            {
                seen = items.ToArray();
                return new BatchAddResult
                {
                    Entries = [MakeAdded("ok.jpg")],
                    Errors = [new BatchAddItemError { Path = "bad.jpg", Code = "GDAL", Message = "corrupt" }]
                };
            });
        var key = new DatasetKey("org", Guid.NewGuid());

        using var ticket = queue.BeginUpload(key);
        var outcomes = await queue.EnqueueAsync(key, [Hinted("ok.jpg"), Hinted("bad.jpg")], ticket);

        seen!.Single(i => i.Path == "ok.jpg").Hint!.Sha256.ShouldBe("sha-ok.jpg");
        outcomes[0].Entry!.Path.ShouldBe("ok.jpg");
        outcomes[0].Error.ShouldBeNull();
        outcomes[1].Entry.ShouldBeNull();
        outcomes[1].Error.ShouldBeOfType<DdbException>().Message.ShouldContain("corrupt");
    }

    [Test]
    public async Task PathOverload_StillFailsOnTheFirstError()
    {
        var (queue, ddb) = CreateQueue();
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns(new BatchAddResult { Errors = [new BatchAddItemError { Path = "bad.jpg", Code = "FS", Message = "gone" }] });

        await Should.ThrowAsync<DdbException>(() =>
            queue.EnqueueAsync(new DatasetKey("org", Guid.NewGuid()), "bad.jpg"));
    }
}
