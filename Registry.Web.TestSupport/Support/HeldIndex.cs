using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Test.Support;

/// <summary>
/// The native index of a dataset behind a real <c>DatasetIndexQueue</c>: every batch indexes its files, unless the test
/// holds it (<see cref="HoldNext"/>). A held batch starts, waits until the test ends it, then indexes its files or is
/// busy, as a native write on an index that another process keeps locked past the libddb retry deadline is
/// (<see cref="DdbBusyException"/>, and again on the queue's own retry of the batch).
/// </summary>
public sealed class HeldIndex
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Queue<Hold> _holds = new();
    private readonly HashSet<string> _indexed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _writes = new(StringComparer.Ordinal);
    private readonly Action<string>? _onIndexed;
    private int _busy;

    /// <summary>Answers the batch writes and the entry lookups of <paramref name="ddb"/>.</summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="onIndexed">Told every path a batch indexes (a test that keeps an index of its own).</param>
    public HeldIndex(Mock<IDDB> ddb, Action<string>? onIndexed = null)
    {
        _onIndexed = onIndexed;
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<AddItem>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<AddItem> items, bool _) => Write([.. items.Select(i => i.Path)]));
        ddb.Setup(d => d.AddRawBatchWithOptions(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<string> paths, bool _) => Write([.. paths]));
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns<string>(IsIndexed);
    }

    /// <summary>Whether a batch indexed <paramref name="path"/>.</summary>
    public bool IsIndexed(string path)
    {
        lock (_gate) return _indexed.Contains(path);
    }

    /// <summary>How many batches indexed <paramref name="path"/>.</summary>
    public int WritesOf(string path)
    {
        lock (_gate) return _writes.GetValueOrDefault(path);
    }

    /// <summary>Holds the next batch.</summary>
    public Hold HoldNext()
    {
        var hold = new Hold(this);
        lock (_gate) _holds.Enqueue(hold);
        return hold;
    }

    private BatchAddResult Write(string[] paths)
    {
        Hold? hold;
        lock (_gate)
        {
            if (_busy > 0)
            {
                _busy--;
                throw new DdbBusyException("database is locked");
            }

            _holds.TryDequeue(out hold);
        }

        if (hold != null && !hold.WaitForEnd())
            throw new DdbBusyException("database is locked");

        lock (_gate)
            foreach (var path in paths)
            {
                _indexed.Add(path);
                _writes[path] = _writes.GetValueOrDefault(path) + 1;
                _onIndexed?.Invoke(path);
            }

        return new BatchAddResult
        {
            Entries = [.. paths.Select(p => new BatchAddedEntry { Path = p, Hash = "h_" + p, Type = EntryType.Generic, Status = "added" })]
        };
    }

    /// <summary>A batch the test holds.</summary>
    public sealed class Hold
    {
        private readonly HeldIndex _index;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Hold(HeldIndex index) => _index = index;

        /// <summary>Completes when the held batch has started writing.</summary>
        public Task Started => _started.Task;

        /// <summary>Ends the held batch: it indexes its files.</summary>
        public void Index() => _end.TrySetResult(true);

        /// <summary>Ends the held batch busy, and the queue's one retry of it too.</summary>
        public void Busy()
        {
            lock (_index._gate) _index._busy++;
            _end.TrySetResult(false);
        }

        // On the batch: true to index its files, false to be busy
        internal bool WaitForEnd()
        {
            _started.TrySetResult();
            if (!_end.Task.Wait(HoldLimit))
                throw new TimeoutException("The test never ended the held batch");
            return _end.Task.Result;
        }
    }
}
