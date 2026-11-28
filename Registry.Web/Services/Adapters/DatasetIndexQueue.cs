using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IDatasetIndexQueue"/>: a per-dataset FIFO lane that coalesces concurrent
/// <see cref="EnqueueAsync(DatasetKey,IReadOnlyList{string},CancellationToken)"/> calls into a single native
/// <see cref="IDdbIndex.AddRawBatchWithOptions(System.Collections.Generic.IReadOnlyList{string},bool)"/> call. Registered as a singleton (one set of lanes
/// for the whole process); resolves the scoped <see cref="IDdbManager"/> via
/// <see cref="IServiceScopeFactory"/> once per batch, not once per file.
/// </summary>
public sealed class DatasetIndexQueue : IDatasetIndexQueue, IDisposable
{
    private sealed class IndexRequest
    {
        public required AddItem Item { get; init; }
        public string Path => Item.Path;
        public required TaskCompletionSource<Entry> Tcs { get; init; }

        /// <summary>Enqueued by an upload registered with <see cref="BeginUpload"/>.</summary>
        public bool Tracked { get; init; }

        private const int Waiting = 0, InBatch = 1, Dropped = 2;
        private int _state;

        /// <summary>
        /// Taken into a native batch (by the drain loop, right before the batch is written): from now on the
        /// request is applied or fails there, it can no longer be dropped. False when the caller dropped it.
        /// </summary>
        public bool TryTake() => Interlocked.CompareExchange(ref _state, InBatch, Waiting) == Waiting;

        /// <summary>
        /// Given up by its caller before a batch took it: it is never applied (the drain loop skips it) and its
        /// task is canceled. False when a batch already took it.
        /// </summary>
        public bool TryDrop()
        {
            if (Interlocked.CompareExchange(ref _state, Dropped, Waiting) != Waiting)
                return false;
            Tcs.TrySetCanceled();
            return true;
        }
    }

    private sealed class DatasetLane
    {
        public required DatasetKey Key { get; init; }
        public required Channel<IndexRequest> Channel { get; init; }
        public required Task DrainLoop { get; set; }

        /// <summary>
        /// Set when the lane is being retired (explicit <see cref="Release"/> or idle trim).
        /// Readers recreate a fresh lane instead of enqueueing into a lane whose drain loop
        /// may no longer exist (a write into a dead lane would hang until the enqueue timeout).
        /// </summary>
        public volatile bool Retired;

        /// <summary>Timestamp (<see cref="TimeProvider.GetTimestamp"/>) of the last enqueue activity (best-effort, for idle trimming).</summary>
        public long LastActivityTimestamp;

        /// <summary>Uploads registered with <see cref="BeginUpload"/> that have not enqueued yet.</summary>
        public int Streaming;

        private TaskCompletionSource _streamingChanged = NewSignal();

        /// <summary>
        /// Completes the next time <see cref="Streaming"/> drops. Read it before checking
        /// <see cref="Streaming"/>, so a change between the check and the wait is not missed.
        /// </summary>
        public Task StreamingChanged => Volatile.Read(ref _streamingChanged).Task;

        public void EndStreaming()
        {
            Interlocked.Decrement(ref Streaming);
            Interlocked.Exchange(ref _streamingChanged, NewSignal()).TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LaneTicket(DatasetLane lane) : UploadTicket
    {
        public DatasetLane Lane { get; } = lane;
        protected override void OnEnded() => Lane.EndStreaming();
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatasetIndexQueue> _logger;
    private readonly IndexQueueSettings _opts;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<DatasetKey, DatasetLane> _lanes = new();
    private readonly CancellationTokenSource _shutdown = new();

    private const int MaxIdleLaneTrimSeconds = 86_400;

    /// <param name="time">Clock of the batch window, the idle trim, the enqueue timeout and the
    /// busy-retry delay; <see cref="TimeProvider.System"/> when null.</param>
    public DatasetIndexQueue(IServiceScopeFactory scopeFactory, ILogger<DatasetIndexQueue> logger,
        IOptions<IndexQueueSettings> settings, TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _opts = settings.Value;
        ValidateSettings(_opts);
    }

    // Fail fast at startup: a non-positive tuning value would make every enqueue on this
    // process hang (zero window/timeout) or commit an empty batch, instead of surfacing
    // the misconfiguration at boot.
    private static void ValidateSettings(IndexQueueSettings o)
    {
        if (o.BatchWindowMs <= 0)
            throw new ArgumentException(
                $"{nameof(IndexQueueSettings)}.{nameof(IndexQueueSettings.BatchWindowMs)} must be > 0, got {o.BatchWindowMs}.",
                nameof(o));
        if (o.MaxBatchSize <= 0)
            throw new ArgumentException(
                $"{nameof(IndexQueueSettings)}.{nameof(IndexQueueSettings.MaxBatchSize)} must be > 0, got {o.MaxBatchSize}.",
                nameof(o));
        if (o.EnqueueTimeoutSeconds <= 0)
            throw new ArgumentException(
                $"{nameof(IndexQueueSettings)}.{nameof(IndexQueueSettings.EnqueueTimeoutSeconds)} must be > 0, got {o.EnqueueTimeoutSeconds}.",
                nameof(o));
        // Upper bound keeps TimeSpan.FromSeconds() below the CancellationTokenSource delay limit
        // used by the drain loop's idle deadline.
        if (o.IdleLaneTrimSeconds is <= 0 or > MaxIdleLaneTrimSeconds)
            throw new ArgumentException(
                $"{nameof(IndexQueueSettings)}.{nameof(IndexQueueSettings.IdleLaneTrimSeconds)} must be in (0, {MaxIdleLaneTrimSeconds}], got {o.IdleLaneTrimSeconds}.",
                nameof(o));
    }

    public async Task<IReadOnlyList<Entry>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        var outcomes = await EnqueueAsync(dataset, paths.Select(p => new AddItem(p)).ToArray(), null, ct);

        // All or nothing for path callers: the first failure in input order, as Task.WhenAll
        foreach (var outcome in outcomes)
            if (outcome.Error != null)
                ExceptionDispatchInfo.Throw(outcome.Error);

        return outcomes.Select(o => o.Entry!).ToArray();
    }

    public UploadTicket BeginUpload(DatasetKey dataset)
    {
        var lane = AcquireLane(dataset);
        Interlocked.Increment(ref lane.Streaming);
        Volatile.Write(ref lane.LastActivityTimestamp, _time.GetTimestamp());
        return new LaneTicket(lane);
    }

    public async Task<IReadOnlyList<IndexOutcome>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<AddItem> items,
        UploadTicket? upload, CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            upload?.End();
            return [];
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.EnqueueTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _shutdown.Token);

        // Every request made for each item, on every lane it was written to (the latest last): when the caller
        // gives up, those no batch took yet are dropped, so nothing is applied after the caller stopped waiting
        var requests = items.Select(_ => new List<IndexRequest>(1)).ToArray();

        try
        {
            while (true)
            {
                // Bounds the retry-on-retirement loop below by the caller deadline.
                linked.Token.ThrowIfCancellationRequested();

                var lane = AcquireLane(dataset);
                Volatile.Write(ref lane.LastActivityTimestamp, _time.GetTimestamp());

                var tasks = new Task<Entry>[items.Count];
                var accepted = 0;
                try
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var tcs = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var request = new IndexRequest { Item = items[i], Tcs = tcs, Tracked = upload != null };
                        tasks[i] = tcs.Task;
                        requests[i].Add(request);

                        // Backpressure: if the lane's channel is full, this await blocks the caller
                        // (does not throw or silently drop) until room is available or the
                        // deadline/cancellation fires.
                        await lane.Channel.Writer.WriteAsync(request, linked.Token);
                        accepted = i + 1;
                    }
                }
                catch (ChannelClosedException)
                {
                    // The lane retired mid-write: what it accepted and no batch took yet is dropped, what a batch
                    // took commits (or fails) there and nobody needs its result; the whole set is replayed on a
                    // fresh lane. Re-adding an already-committed path is safe - the native layer reports it as
                    // unchanged.
                    for (var i = 0; i < accepted; i++)
                        if (!requests[i][^1].TryDrop())
                            ObserveAbandoned(tasks[i]);
                    continue;
                }

                // After the writes: the drain loop sees this upload's files before it stops waiting
                // for it, so they commit in the same batch
                upload?.End();

                // Guard the await too, not just the channel writes: once the whole set is
                // accepted, a stalled native batch must still respect the deadline/cancellation
                // instead of hanging the caller (the catch below drops what is still waiting).
                try
                {
                    await Task.WhenAll(tasks).WaitAsync(linked.Token);
                }
                catch (Exception) when (tasks.All(t => t.IsCompleted))
                {
                    // Item failures: reported per item below
                }

                return tasks.Select(t => t.IsCompletedSuccessfully
                        ? new IndexOutcome(t.Result, null)
                        : new IndexOutcome(null, t.Exception?.InnerException ?? t.Exception ??
                            (Exception)new OperationCanceledException("Index request canceled")))
                    .ToArray();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            upload?.End();
            var outcomes = requests.Select(GiveUp).ToArray();

            // Canceled by the caller or by the shutdown: what was not applied never will be
            if (!timeout.IsCancellationRequested || ct.IsCancellationRequested)
                throw;

            // The deadline: each item says what became of it, so the caller knows which files the index will
            // never list (safe to take out) and which a batch is still writing (to be left in place)
            var late = new TransientException(
                $"Index write for dataset '{dataset.OrgSlug}/{dataset.InternalRef}' did not complete within " +
                $"{_opts.EnqueueTimeoutSeconds}s");
            return outcomes.Select(o => o ?? new IndexOutcome(null, late)).ToArray();
        }
    }

    /// <summary>
    /// What became of an item whose caller stopped waiting: its entry when a request of it was applied, the error
    /// of its latest request when that one failed, an <see cref="IndexWritePendingException"/> while a batch that
    /// took it is still running; null when it was dropped before any batch took it (it is never applied). Every
    /// request no batch took is dropped first.
    /// </summary>
    private static IndexOutcome? GiveUp(List<IndexRequest> requestsOfItem)
    {
        var pending = false;
        foreach (var request in requestsOfItem)
        {
            if (request.TryDrop())
                continue;

            var task = request.Tcs.Task;
            if (task.IsCompletedSuccessfully)
                return new IndexOutcome(task.Result, null);
            if (!task.IsCompleted)
            {
                pending = true;
                ObserveAbandoned(task);
            }
        }

        if (pending)
            return new IndexOutcome(null, new IndexWritePendingException(requestsOfItem[^1].Path));

        var latest = requestsOfItem.Count > 0 ? requestsOfItem[^1].Tcs.Task : null;
        return latest is { IsFaulted: true }
            ? new IndexOutcome(null, latest.Exception!.InnerException ?? latest.Exception)
            : null;
    }

    /// <summary>
    /// Returns a lane that is registered and not retired, recreating it if needed. A write into a
    /// retired lane would never be drained, so a retired candidate is never handed out.
    /// </summary>
    private DatasetLane AcquireLane(DatasetKey dataset)
    {
        while (true)
        {
            var candidate = _lanes.GetOrAdd(dataset, CreateLane);
            if (!candidate.Retired)
                return candidate;

            // Evict the exact retired instance only: an unconditional TryRemove(key) would drop a
            // healthy replacement already installed by a concurrent enqueuer, leaving two drain
            // loops writing to the same dataset.
            _lanes.TryRemove(new KeyValuePair<DatasetKey, DatasetLane>(dataset, candidate));
        }
    }

    // A request a batch took is completed by that batch, but nobody awaits it anymore; observe its
    // fault so it never surfaces as UnobservedTaskException.
    private static void ObserveAbandoned(Task<Entry> task) =>
        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    public void Release(DatasetKey dataset)
    {
        if (_lanes.TryRemove(dataset, out var lane))
        {
            lane.Retired = true;

            // Completing the writer is what lets the lane actually go away: the drain loop commits
            // whatever is still queued, then observes the closed channel and exits. Without it the
            // loop would park on WaitToReadAsync forever, since no writer can reach an unregistered
            // lane again. A new enqueue transparently recreates the lane.
            lane.Channel.Writer.TryComplete();
            _logger.LogDebug("Released index lane for {Org}/{Ref} (dataset removal)",
                dataset.OrgSlug, dataset.InternalRef);
        }
    }

    private DatasetLane CreateLane(DatasetKey key)
    {
        var channel = Channel.CreateBounded<IndexRequest>(
            new BoundedChannelOptions(Math.Max(1, _opts.QueueCapacityPerDataset))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        var lane = new DatasetLane
        {
            Key = key,
            Channel = channel,
            DrainLoop = Task.CompletedTask,
            LastActivityTimestamp = _time.GetTimestamp()
        };
        lane.DrainLoop = Task.Run(() => DrainLoopAsync(lane));
        return lane;
    }

    private async Task DrainLoopAsync(DatasetLane lane)
    {
        var reader = lane.Channel.Reader;

        // The drain loop must never die: a crashed lane would hang every future request for
        // this dataset (the lane stays registered in _lanes but nothing ever reads its
        // channel again). Any exception escaping a single iteration is caught, logged, and the
        // loop continues with the next batch.
        while (!_shutdown.IsCancellationRequested)
        {
            // Idle trim: release the lane when it holds no work and has had no enqueues for
            // IdleLaneTrimSeconds - per-dataset lanes are long-lived singletons otherwise.
            if (await TryTrimIfIdleAsync(lane, reader))
                return;

            var batch = new List<IndexRequest>(_opts.MaxBatchSize);
            try
            {
                // The wait needs a deadline, otherwise the loop parks here forever and the trim
                // check above is never re-evaluated on a quiet dataset.
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.IdleLaneTrimSeconds), _time);
                using var idleLinked =
                    CancellationTokenSource.CreateLinkedTokenSource(idle.Token, _shutdown.Token);
                try
                {
                    if (!await reader.WaitToReadAsync(idleLinked.Token))
                        break; // channel completed by Release or by retirement
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested &&
                                                        !_shutdown.IsCancellationRequested)
                {
                    continue; // idle deadline elapsed - re-evaluate the trim check
                }

                if (reader.TryRead(out var first))
                    batch.Add(first);

                // Greedily coalesce whatever is already queued, then wait a short window for
                // stragglers from other concurrently-enqueuing callers. A batch made only of
                // registered uploads stops waiting as soon as no registered upload is still
                // streaming: nothing else can arrive for it.
                using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(_opts.BatchWindowMs), _time);
                using var windowLinked =
                    CancellationTokenSource.CreateLinkedTokenSource(window.Token, _shutdown.Token);
                var untracked = batch.Count(r => !r.Tracked);
                while (batch.Count < _opts.MaxBatchSize)
                {
                    if (reader.TryRead(out var next))
                    {
                        batch.Add(next);
                        if (!next.Tracked) untracked++;
                        continue;
                    }

                    var streamingChanged = lane.StreamingChanged;
                    if (untracked == 0 && Volatile.Read(ref lane.Streaming) <= 0)
                        break;

                    try
                    {
                        var readable = reader.WaitToReadAsync(windowLinked.Token).AsTask();
                        var woke = untracked == 0
                            ? await Task.WhenAny(readable, streamingChanged.WaitAsync(windowLinked.Token))
                            : readable;
                        if (woke == readable && !await readable)
                            break;
                        await woke; // rethrows the window's cancellation
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dataset index queue drain loop failed reading lane for {Org}/{Ref}",
                    lane.Key.OrgSlug, lane.Key.InternalRef);

                // Requests already pulled out of the lane must not be discarded: failing
                // their TCSs is what keeps callers off a forever hang (they await
                // Task.WhenAll and would otherwise never observe a result). Transient,
                // because the batch was never even attempted - a retry is the remedy.
                foreach (var req in batch)
                    req.Tcs.TrySetException(new TransientException(
                        $"Dataset index queue failed reading '{lane.Key.OrgSlug}/{lane.Key.InternalRef}'; retry the request",
                        ex));
                continue;
            }

            if (batch.Count == 0)
                continue;

            try
            {
                await CommitBatchAsync(lane.Key, batch);
            }
            catch (Exception ex)
            {
                // CommitBatchAsync is expected to complete every TCS itself (success or
                // failure). If it throws instead, treat it as a bug: fail every request in
                // this batch rather than leaving them pending forever.
                _logger.LogError(ex, "Dataset index queue: unhandled failure committing batch for {Org}/{Ref}",
                    lane.Key.OrgSlug, lane.Key.InternalRef);
                foreach (var req in batch)
                    req.Tcs.TrySetException(ex);
            }
        }

        // Shutdown: fail whatever is left so callers do not hang forever.
        while (reader.TryRead(out var leftover))
            leftover.Tcs.TrySetException(new OperationCanceledException("Index queue is shutting down"));
    }

    /// <summary>
    /// Retires the lane when it holds no work and has seen no enqueue activity for
    /// <see cref="IndexQueueSettings.IdleLaneTrimSeconds"/>. Called from the drain loop only, so
    /// the single-reader contract of the channel still holds while leftovers are drained.
    /// </summary>
    private async Task<bool> TryTrimIfIdleAsync(DatasetLane lane, ChannelReader<IndexRequest> reader)
    {
        if (reader.TryPeek(out _) || Volatile.Read(ref lane.Streaming) > 0)
            return false; // work queued or announced - not idle
        var idle = _time.GetElapsedTime(Volatile.Read(ref lane.LastActivityTimestamp));
        if (idle < TimeSpan.FromSeconds(_opts.IdleLaneTrimSeconds))
            return false;

        lane.Retired = true;
        _lanes.TryRemove(new KeyValuePair<DatasetKey, DatasetLane>(lane.Key, lane));

        // Closing the writer is what makes retirement atomic against enqueue: once it returns, no
        // further write can be accepted, so what is read below is the complete set of requests
        // that slipped past the Retired check. Writers still blocked in WriteAsync get a
        // ChannelClosedException and replay on a fresh lane.
        lane.Channel.Writer.TryComplete();

        var leftover = new List<IndexRequest>();
        while (reader.TryRead(out var req))
            leftover.Add(req);

        if (leftover.Count > 0)
        {
            try
            {
                await CommitBatchAsync(lane.Key, leftover);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Dataset index queue: unhandled failure committing retirement batch for {Org}/{Ref}",
                    lane.Key.OrgSlug, lane.Key.InternalRef);
                foreach (var req in leftover)
                    req.Tcs.TrySetException(ex);
            }
        }

        _logger.LogDebug("Released idle index lane for {Org}/{Ref} after {IdleSec}s",
            lane.Key.OrgSlug, lane.Key.InternalRef, _opts.IdleLaneTrimSeconds);
        return true;
    }

    private async Task CommitBatchAsync(DatasetKey key, List<IndexRequest> batch)
    {
        // Taken right before the native write: a request its caller dropped meanwhile (it stopped waiting and may
        // have taken the file out again) is never applied
        batch = batch.Where(r => r.TryTake()).ToList();
        if (batch.Count == 0)
            return;

        // Last writer wins per path within a batch: keep only each path's most recent request,
        // but complete every duplicate request's TCS with the same resulting Entry.
        var byPath = new Dictionary<string, List<IndexRequest>>();
        foreach (var req in batch)
        {
            if (!byPath.TryGetValue(req.Path, out var list))
                byPath[req.Path] = list = [];
            list.Add(req);
        }

        var paths = byPath.Keys.ToList();
        // The latest request of a path also carries the latest hash hint
        var items = byPath.Values.Select(list => list[^1].Item).ToList();
        var hinted = items.Any(i => i.Hint != null);

        using var scope = _scopeFactory.CreateScope();
        var ddbManager = scope.ServiceProvider.GetRequiredService<IDdbManager>();

        BatchAddResult result;
        try
        {
            // Narrowed to the index role interface only - this coalescer never needs build,
            // meta, raster or analytics operations.
            IDdbIndex ddb = ddbManager.Get(key.OrgSlug, key.InternalRef);
            result = hinted
                ? ddb.AddRawBatchWithOptions(items, stopOnError: false)
                : ddb.AddRawBatchWithOptions(paths, stopOnError: false);
        }
        catch (DdbBusyException ex)
        {
            // The native layer already retried to its own deadline. Retry the whole batch once
            // more with a fresh jittered delay; if it fails again, fail every request as
            // transient (never SetResult(null), always SetException so the original cause is
            // preserved for the caller/ApiExceptionFilter).
            _logger.LogWarning(ex, "Dataset index queue: busy committing batch for {Org}/{Ref}, retrying once",
                key.OrgSlug, key.InternalRef);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 250)), _time);
                IDdbIndex ddb = ddbManager.Get(key.OrgSlug, key.InternalRef);
                result = hinted
                ? ddb.AddRawBatchWithOptions(items, stopOnError: false)
                : ddb.AddRawBatchWithOptions(paths, stopOnError: false);
            }
            catch (Exception retryEx)
            {
                var transient = new TransientException(
                    $"Dataset '{key.OrgSlug}/{key.InternalRef}' index is busy; retry the request", retryEx,
                    retryAfterSeconds: 2);
                foreach (var req in batch)
                    req.Tcs.TrySetException(transient);
                return;
            }
        }
        catch (Exception ex)
        {
            // Any other exception (schema corruption, disk full, ...) is database-scoped: fail
            // every request in the batch with the original exception preserved.
            foreach (var req in batch)
                req.Tcs.TrySetException(ex);
            return;
        }

        var handled = new HashSet<string>();

        foreach (var e in result.Entries)
        {
            handled.Add(e.Path);
            if (byPath.TryGetValue(e.Path, out var requests))
                foreach (var req in requests)
                    req.Tcs.TrySetResult(e);
        }

        foreach (var u in result.Unchanged)
        {
            handled.Add(u.Path);
            if (!byPath.TryGetValue(u.Path, out var requests))
                continue;

            // Resolve the existing Entry so callers that need type/hash still get one
            // (only for cache-hit paths, not on the contention-heavy new-file path).
            Entry? entry = null;
            try
            {
                var ddb = ddbManager.Get(key.OrgSlug, key.InternalRef);
                entry = ddb.GetEntry(u.Path);
            }
            catch (Exception ex)
            {
                foreach (var req in requests)
                    req.Tcs.TrySetException(ex);
                continue;
            }

            if (entry == null)
            {
                foreach (var req in requests)
                    req.Tcs.TrySetException(
                        new DdbException($"'{u.Path}' reported unchanged but has no index entry"));
                continue;
            }

            foreach (var req in requests)
                req.Tcs.TrySetResult(entry);
        }

        foreach (var e in result.Errors)
        {
            handled.Add(e.Path);
            if (byPath.TryGetValue(e.Path, out var requests))
                foreach (var req in requests)
                    req.Tcs.TrySetException(e.ToException());
        }

        // Completeness contract: every path must land in exactly one bucket. A path absent
        // from all three is an adapter bug, not silent success - fail it loudly rather than
        // leaving its caller hanging until EnqueueTimeoutSeconds.
        foreach (var path in paths)
        {
            if (handled.Contains(path)) continue;
            if (!byPath.TryGetValue(path, out var requests)) continue;
            foreach (var req in requests)
                req.Tcs.TrySetException(
                    new DdbException($"'{path}' was not reported by the native batch add (adapter bug)"));
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}

/// <summary>
/// The caller stopped waiting while a native batch that took the file was still being written: the outcome is not
/// known yet. The file must stay where it is: the batch lists it when it commits, and the reconciliation sweep
/// indexes it when that batch fails. Transient (HTTP 503 with <c>Retry-After</c>) for the client.
/// </summary>
public sealed class IndexWritePendingException(string path)
    : TransientException($"The index write of '{path}' is still running; its outcome is not known yet")
{
    /// <summary>The dataset path whose index write is still running.</summary>
    public string Path { get; } = path;
}
