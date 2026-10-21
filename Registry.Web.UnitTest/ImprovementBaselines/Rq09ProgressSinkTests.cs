#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-09: a heavy tool reports progress from any thread (native progress callbacks run on worker
/// threads). The sink writes the JobIndex through one writer of its own, one write at a time, and a
/// report never waits for the database: before, every report blocked on
/// <c>UpdateProgressAsync(...).GetAwaiter().GetResult()</c> on the task's scoped DbContext, so two
/// reports (or a report and the wrapper's own writes) used one DbContext at once.
/// </summary>
[TestFixture]
public class Rq09ProgressSinkTests
{
    [ThreadStatic] private static bool _reporting;

    /// <summary>A JobIndex writer that records how many writes run at once and can hold the first one.</summary>
    private sealed class RecordingWriter
    {
        private int _inside;
        private int _entries;
        public int MaxConcurrent;
        public readonly ManualResetEventSlim FirstWriteGate = new(true);
        public readonly ConcurrentQueue<(int? Percent, string? Phase, string? Tail)> Writes = new();
        public Action? OnEntry;

        public Mock<IJobIndexWriter> Mock()
        {
            var writer = new Mock<IJobIndexWriter>();
            writer.Setup(w => w.UpdateProgressAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns<string, int?, string?, string?, DateTime, CancellationToken>((_, percent, phase, tail, _, _) =>
                {
                    var now = Interlocked.Increment(ref _inside);
                    InterlockedMax(ref MaxConcurrent, now);
                    OnEntry?.Invoke();
                    if (Interlocked.Increment(ref _entries) == 1)
                        FirstWriteGate.Wait(TimeSpan.FromSeconds(30));
                    Writes.Enqueue((percent, phase, tail));
                    Interlocked.Decrement(ref _inside);
                    return Task.CompletedTask;
                });
            return writer;
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value &&
                   Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    private static IServiceScopeFactory Scopes(IJobIndexWriter writer) =>
        new ServiceCollection().AddScoped(_ => writer).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static HangfireProgressSink Sink(IJobIndexWriter writer, LogRingBuffer? tail = null, int throttle = 0) =>
        new(null, Scopes(writer), tail ?? new LogRingBuffer(), "task-1", throttle,
            Mock.Of<ILogger<HangfireProgressSink>>());

    [Test]
    public async Task ReportsFromManyThreads_NeverWriteConcurrently_AndNeverWaitForTheDatabase()
    {
        const int reporters = 8;
        var recorder = new RecordingWriter();
        recorder.FirstWriteGate.Reset();
        var writer = recorder.Mock();
        var sink = Sink(writer.Object);

        // The writer stays held until every report has either returned or entered the writer (a
        // report that waits for the database enters it)
        var progressed = 0;
        using var allProgressed = new ManualResetEventSlim();
        void Progressed()
        {
            if (Interlocked.Increment(ref progressed) >= reporters)
                allProgressed.Set();
        }
        // Only a reporting thread that entered the writer counts (it waits for the database)
        recorder.OnEntry = () =>
        {
            if (_reporting) Progressed();
        };

        var returned = 0;
        var reports = Enumerable.Range(0, reporters).Select(i => Task.Run(() =>
        {
            _reporting = true;
            sink.Report(new HeavyToolProgress(i / (double)reporters, "working", LogChunk: $"line {i}"));
            _reporting = false;
            Interlocked.Increment(ref returned);
            Progressed();
        })).ToArray();

        allProgressed.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        var returnedWhileTheWriterWasHeld = Volatile.Read(ref returned);
        recorder.FirstWriteGate.Set();
        await Task.WhenAll(reports);
        await sink.CompleteAsync();

        returnedWhileTheWriterWasHeld.ShouldBe(reporters, "no report waits for the database");
        recorder.MaxConcurrent.ShouldBe(1, "one write at a time");
        // The final write carries every line
        var last = recorder.Writes.Last();
        for (var i = 0; i < reporters; i++)
            last.Tail.ShouldNotBeNull().ShouldContain($"line {i}");
    }

    [Test]
    public async Task Complete_WritesTheFinalTail_AfterThePendingWrites()
    {
        var recorder = new RecordingWriter();
        var tail = new LogRingBuffer();
        var sink = Sink(recorder.Mock().Object, tail, throttle: 3600);

        sink.Report(new HeavyToolProgress(0.1, "loading", LogChunk: "first"));
        sink.Report(new HeavyToolProgress(0.5, "loading", LogChunk: "second")); // throttled
        sink.Report(new HeavyToolProgress(-1, "done", Message: "finished"));    // phase change
        await sink.CompleteAsync();

        var writes = recorder.Writes.ToArray();
        writes.Length.ShouldBeGreaterThanOrEqualTo(2);
        writes[^1].Phase.ShouldBe("done");
        writes[^1].Tail.ShouldNotBeNull().ShouldContain("finished");
        tail.Snapshot().Lines.Select(l => l.Msg).ShouldBe(["first", "second", "finished"]);
    }

    [Test]
    public async Task AFailingWrite_IsTelemetryOnly()
    {
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.UpdateProgressAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var sink = Sink(writer.Object);

        Should.NotThrow(() => sink.Report(new HeavyToolProgress(0.5, "x", LogChunk: "line")));
        await Should.NotThrowAsync(() => sink.CompleteAsync());
    }
}
