using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using static Registry.Web.Test.DatasetIndexQueueTests;

namespace Registry.Web.Test;

/// <summary>
/// C.5 and E.1 on the index queue: uploads registered with BeginUpload do not wait for the
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

    // The queues below run on a clock that only the test moves: a batch that commits while the
    // clock stands still did not wait for its window
    private static readonly TimeSpan LongWindowSpan = TimeSpan.FromMilliseconds(LongWindow.BatchWindowMs);

    [Test]
    public async Task SingleRegisteredUpload_CommitsWithoutWaitingForTheWindow()
    {
        var time = new FakeTime(DateTime.UtcNow);
        var (queue, ddb) = CreateQueue(LongWindow, time);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        using var ticket = queue.BeginUpload(key);
        var outcomes = await queue.EnqueueAsync(key, [Hinted("a.jpg")], ticket);

        outcomes.Single().Entry!.Path.ShouldBe("a.jpg");
        ticket.End().ShouldBeFalse("the commit ended the upload");
    }

    [Test]
    public async Task UnregisteredCallers_KeepTheWindow()
    {
        var settings = new IndexQueueSettings { BatchWindowMs = 300, MaxBatchSize = 64 };
        var window = TimeSpan.FromMilliseconds(settings.BatchWindowMs);
        var time = new FakeTime(DateTime.UtcNow);
        var (queue, ddb) = CreateQueue(settings, time);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);

        var windowArmed = time.WhenTimerArmed(window);
        var done = queue.EnqueueAsync(new DatasetKey("org", Guid.NewGuid()), "a.txt");
        await windowArmed;

        // The legacy path cannot know whether more callers are coming: it still waits
        time.Advance(window - TimeSpan.FromMilliseconds(1));
        done.IsCompleted.ShouldBeFalse();
        batches.ShouldBeEmpty();

        time.Advance(TimeSpan.FromMilliseconds(1));
        (await done).Path.ShouldBe("a.txt");
    }

    [Test]
    public async Task UploadStillStreaming_KeepsTheBatchOpenUntilItEnqueues()
    {
        var time = new FakeTime(DateTime.UtcNow);
        var (queue, ddb) = CreateQueue(LongWindow, time);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        using var first = queue.BeginUpload(key);
        using var second = queue.BeginUpload(key);

        var windowArmed = time.WhenTimerArmed(LongWindowSpan);
        var firstDone = queue.EnqueueAsync(key, [Hinted("a.jpg")], first);
        // The batch holds the first upload's file and waits: the second upload is still streaming...
        await windowArmed;
        firstDone.IsCompleted.ShouldBeFalse();

        // ...and both commit in one batch as soon as it enqueues
        var secondDone = queue.EnqueueAsync(key, [Hinted("b.jpg")], second);
        await Task.WhenAll(firstDone, secondDone);

        batches.Count.ShouldBe(1);
        batches[0].Select(i => i.Path).ShouldBe(["a.jpg", "b.jpg"], ignoreOrder: true);
    }

    [Test]
    public async Task UploadEndingWithoutFiles_ReleasesTheBatch()
    {
        var time = new FakeTime(DateTime.UtcNow);
        var (queue, ddb) = CreateQueue(LongWindow, time);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        using var uploading = queue.BeginUpload(key);
        var failed = queue.BeginUpload(key);

        var windowArmed = time.WhenTimerArmed(LongWindowSpan);
        var done = queue.EnqueueAsync(key, [Hinted("a.jpg")], uploading);
        await windowArmed;
        done.IsCompleted.ShouldBeFalse();

        // The other request failed before enqueuing anything: disposing its ticket wakes the batch
        failed.Dispose();
        (await done).Single().Entry!.Path.ShouldBe("a.jpg");
        batches.Count.ShouldBe(1);
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
        outcomes[1].Error.ShouldBeOfType<DdbInvalidContentException>().Message.ShouldContain("corrupt");
    }

    /// <summary>
    /// A file still waiting for its batch when the deadline passes is dropped: no batch applies it afterwards, so the
    /// caller may take the file out again (red before: the request stayed in the lane and its batch indexed a file
    /// the upload had already withdrawn, an entry without its file).
    /// </summary>
    [Test]
    public async Task AtTheDeadline_AFileNoBatchTookYet_IsDropped_AndNeverIndexed()
    {
        var settings = new IndexQueueSettings { BatchWindowMs = 600_000, MaxBatchSize = 64, EnqueueTimeoutSeconds = 5 };
        var window = TimeSpan.FromMilliseconds(settings.BatchWindowMs);
        var time = new FakeTime(DateTime.UtcNow);
        var (queue, ddb) = CreateQueue(settings, time);
        var batches = new List<IReadOnlyList<AddItem>>();
        AcceptAll(ddb, batches);
        var key = new DatasetKey("org", Guid.NewGuid());

        // Not registered as an upload: the batch keeps its window open, past the deadline of the caller
        var windowArmed = time.WhenTimerArmed(window);
        var late = queue.EnqueueAsync(key, [Hinted("late.jpg")], null);
        await windowArmed;

        time.Advance(TimeSpan.FromSeconds(settings.EnqueueTimeoutSeconds));
        var outcome = (await late).Single();

        outcome.Entry.ShouldBeNull();
        outcome.Error.ShouldBeOfType<TransientException>();

        // The window ends with nothing left to write; the next upload commits alone
        time.Advance(window);
        using var ticket = queue.BeginUpload(key);
        (await queue.EnqueueAsync(key, [Hinted("next.jpg")], ticket)).Single().Entry!.Path.ShouldBe("next.jpg");
        lock (batches)
            batches.SelectMany(b => b).Select(i => i.Path).ShouldBe(["next.jpg"]);
    }

    /// <summary>
    /// A file a native batch is writing when the deadline passes cannot be dropped any more: its outcome is
    /// reported as pending (a transient error, so the client retries), which tells the upload to leave the file in
    /// place, and the batch still indexes it (red before: a plain timeout, after which the upload withdrew a file the
    /// batch then indexed).
    /// </summary>
    [Test]
    public async Task AtTheDeadline_AFileABatchIsWriting_IsReportedPending_AndTheBatchStillIndexesIt()
    {
        var settings = new IndexQueueSettings { BatchWindowMs = 5000, MaxBatchSize = 64, EnqueueTimeoutSeconds = 5 };
        var time = new FakeTime(DateTime.UtcNow);
        var (queue, ddb) = CreateQueue(settings, time);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        var written = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<AddItem>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<AddItem> items, bool _) =>
            {
                writing.TrySetResult();
                finish.Wait(TimeSpan.FromMinutes(1));
                written.TrySetResult(items.Single().Path);
                return new BatchAddResult { Entries = [.. items.Select(i => MakeAdded(i.Path))] };
            });
        var key = new DatasetKey("org", Guid.NewGuid());

        try
        {
            using var ticket = queue.BeginUpload(key);
            var slow = queue.EnqueueAsync(key, [Hinted("slow.jpg")], ticket);
            await writing.Task;

            time.Advance(TimeSpan.FromSeconds(settings.EnqueueTimeoutSeconds));
            var outcome = (await slow).Single();

            outcome.Entry.ShouldBeNull();
            outcome.Error.ShouldBeOfType<IndexWritePendingException>().Path.ShouldBe("slow.jpg");
            outcome.Error.ShouldBeAssignableTo<TransientException>();
        }
        finally
        {
            finish.Set();
        }

        (await written.Task).ShouldBe("slow.jpg");
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
