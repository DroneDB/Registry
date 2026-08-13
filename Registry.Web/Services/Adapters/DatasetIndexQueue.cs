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
/// <see cref="IDDB.AddRawBatchWithOptions"/> call. Registered as a singleton (one set of lanes
/// for the whole process); resolves the scoped <see cref="IDdbManager"/> via
/// <see cref="IServiceScopeFactory"/> once per batch, not once per file (see
/// ImproveParallelWrites plan, workstream 04 §4.2).
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
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatasetIndexQueue> _logger;
    private readonly IndexQueueSettings _opts;
    private readonly ConcurrentDictionary<DatasetKey, DatasetLane> _lanes = new();
    private readonly CancellationTokenSource _shutdown = new();

    public DatasetIndexQueue(IServiceScopeFactory scopeFactory, ILogger<DatasetIndexQueue> logger,
        IOptions<AppSettings> appSettings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _opts = appSettings.Value.IndexQueue ?? new IndexQueueSettings();
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
            return Array.Empty<Entry>();

        var lane = _lanes.GetOrAdd(dataset, CreateLane);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.EnqueueTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _shutdown.Token);

        var tasks = new Task<Entry>[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            var tcs = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new IndexRequest { Path = paths[i], Tcs = tcs };
            tasks[i] = tcs.Task;

            // Backpressure: if the lane's channel is full, this await blocks the caller (does
            // not throw or silently drop) until room is available or the deadline/cancellation
            // fires.
            await lane.Channel.Writer.WriteAsync(request, linked.Token);
        }

        try
        {
            return await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TransientException(
                $"Index write for dataset '{dataset.OrgSlug}/{dataset.InternalRef}' did not complete within " +
                $"{_opts.EnqueueTimeoutSeconds}s");
        }
    }

    public Task FlushAsync(DatasetKey dataset, CancellationToken ct = default)
    {
        // The drain loop commits everything currently queued on every iteration; enqueueing
        // paths is what waits for a commit, so flushing is naturally expressed as enqueueing
        // zero work and waiting for the lane to have drained at least once. Since there is
        // nothing to enqueue, and per-request completion already guarantees the corresponding
        // batch was committed, an explicit flush of an idle lane is a no-op.
        return _lanes.TryGetValue(dataset, out _) ? Task.CompletedTask : Task.CompletedTask;
    }

    private DatasetLane CreateLane(DatasetKey key)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<IndexRequest>(
            new BoundedChannelOptions(Math.Max(1, _opts.QueueCapacityPerDataset))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        var lane = new DatasetLane { Key = key, Channel = channel, DrainLoop = Task.CompletedTask };
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
            List<IndexRequest> batch;
            try
            {
                if (!await reader.WaitToReadAsync(_shutdown.Token))
                    break; // channel completed (never happens today; no Complete() caller)

                batch = new List<IndexRequest>(_opts.MaxBatchSize);
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

    private async Task CommitBatchAsync(DatasetKey key, List<IndexRequest> batch)
    {
        // Last writer wins per path within a batch: keep only each path's most recent request,
        // but complete every duplicate request's TCS with the same resulting Entry.
        var byPath = new Dictionary<string, List<IndexRequest>>();
        foreach (var req in batch)
        {
            if (!byPath.TryGetValue(req.Path, out var list))
                byPath[req.Path] = list = new List<IndexRequest>();
            list.Add(req);
        }

        var paths = byPath.Keys.ToList();

        using var scope = _scopeFactory.CreateScope();
        var ddbManager = scope.ServiceProvider.GetRequiredService<IDdbManager>();

        BatchAddResult result;
        try
        {
            // Narrowed to the index role interface only - this coalescer never needs build,
            // meta, raster or analytics operations (ImproveParallelWrites plan, workstream 04 §7).
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
        // leaving its caller hanging until EnqueueTimeoutSeconds (see plan §4.2/§5.1).
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
