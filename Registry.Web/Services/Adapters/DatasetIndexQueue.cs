#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IDatasetIndexQueue"/>: a per-dataset FIFO lane that coalesces concurrent
/// <see cref="EnqueueAsync(DatasetKey,string,CancellationToken)"/> calls into a single native
/// <see cref="IDdbIndex.AddRawBatchWithOptions"/> call. Registered as a singleton (one set of lanes
/// for the whole process); resolves the scoped <see cref="IDdbManager"/> via
/// <see cref="IServiceScopeFactory"/> once per batch, not once per file.
/// </summary>
public sealed class DatasetIndexQueue : IDatasetIndexQueue, IDisposable
{
    private sealed class IndexRequest
    {
        public required string Path { get; init; }
        public required TaskCompletionSource<Entry> Tcs { get; init; }
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

        /// <summary>Ticks of the last enqueue activity (best-effort, for idle trimming).</summary>
        public long LastActivityTicks;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatasetIndexQueue> _logger;
    private readonly IndexQueueSettings _opts;
    private readonly ConcurrentDictionary<DatasetKey, DatasetLane> _lanes = new();
    private readonly CancellationTokenSource _shutdown = new();

    private const int MaxIdleLaneTrimSeconds = 86_400;

    public DatasetIndexQueue(IServiceScopeFactory scopeFactory, ILogger<DatasetIndexQueue> logger,
        IOptions<AppSettings> appSettings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _opts = appSettings.Value.IndexQueue ?? new IndexQueueSettings();
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

    public async Task<Entry> EnqueueAsync(DatasetKey dataset, string path, CancellationToken ct = default)
    {
        var results = await EnqueueAsync(dataset, [path], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<Entry>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return [];

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.EnqueueTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _shutdown.Token);

        try
        {
            while (true)
            {
                // Bounds the retry-on-retirement loop below by the caller deadline.
                linked.Token.ThrowIfCancellationRequested();

                var lane = AcquireLane(dataset);
                Volatile.Write(ref lane.LastActivityTicks, Environment.TickCount64);

                var tasks = new Task<Entry>[paths.Count];
                var accepted = 0;
                try
                {
                    for (var i = 0; i < paths.Count; i++)
                    {
                        var tcs = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var request = new IndexRequest { Path = paths[i], Tcs = tcs };
                        tasks[i] = tcs.Task;

                        // Backpressure: if the lane's channel is full, this await blocks the caller
                        // (does not throw or silently drop) until room is available or the
                        // deadline/cancellation fires.
                        await lane.Channel.Writer.WriteAsync(request, linked.Token);
                        accepted = i + 1;
                    }
                }
                catch (ChannelClosedException)
                {
                    // The lane retired mid-write. Whatever it already accepted is committed (or
                    // failed) by the retiring drain loop, so those results are redundant: observe
                    // them and replay the whole set on a fresh lane. Re-adding an already-committed
                    // path is safe - the native layer reports it as unchanged.
                    ObserveAbandoned(tasks, accepted);
                    continue;
                }

                return await Task.WhenAll(tasks);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TransientException(
                $"Index write for dataset '{dataset.OrgSlug}/{dataset.InternalRef}' did not complete within " +
                $"{_opts.EnqueueTimeoutSeconds}s");
        }
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

    // Requests handed to a lane that retired mid-write are completed by that lane, but nobody
    // awaits them anymore; observe faults so they never surface as UnobservedTaskException.
    private static void ObserveAbandoned(Task<Entry>[] tasks, int count)
    {
        for (var i = 0; i < count; i++)
            _ = tasks[i].ContinueWith(static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

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
            LastActivityTicks = Environment.TickCount64
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
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.IdleLaneTrimSeconds));
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
                // stragglers from other concurrently-enqueuing callers.
                using var window = new CancellationTokenSource(_opts.BatchWindowMs);
                using var windowLinked =
                    CancellationTokenSource.CreateLinkedTokenSource(window.Token, _shutdown.Token);
                while (batch.Count < _opts.MaxBatchSize)
                {
                    if (reader.TryRead(out var next))
                    {
                        batch.Add(next);
                        continue;
                    }

                    try
                    {
                        if (!await reader.WaitToReadAsync(windowLinked.Token))
                            break;
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
        if (reader.TryPeek(out _))
            return false; // work still queued - not idle
        var idleMs = Environment.TickCount64 - Volatile.Read(ref lane.LastActivityTicks);
        if (idleMs < _opts.IdleLaneTrimSeconds * 1000L)
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

        using var scope = _scopeFactory.CreateScope();
        var ddbManager = scope.ServiceProvider.GetRequiredService<IDdbManager>();

        BatchAddResult result;
        try
        {
            // Narrowed to the index role interface only - this coalescer never needs build,
            // meta, raster or analytics operations.
            IDdbIndex ddb = ddbManager.Get(key.OrgSlug, key.InternalRef);
            result = ddb.AddRawBatchWithOptions(paths, stopOnError: false);
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
                await Task.Delay(Random.Shared.Next(50, 250));
                IDdbIndex ddb = ddbManager.Get(key.OrgSlug, key.InternalRef);
                result = ddb.AddRawBatchWithOptions(paths, stopOnError: false);
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
                    req.Tcs.TrySetException(new DdbException($"[{e.Code}] {e.Message}"));
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
