using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Default <see cref="ITaskWriteRecorder"/> of a running task: buffers what the tool is about to create and writes it to
/// the task's write record (<see cref="ITaskWriteRecordStore"/>) in batches. A tool writes a path only once it is in the
/// stored record (§8.1): it records a batch of paths, saves them with one <see cref="FlushAsync"/>, then creates them.
/// What is still buffered when the task fails or is cancelled (paths, folders and stamps) is handed to the rollback
/// (<see cref="Pending"/>).
/// </summary>
public sealed class TaskWriteRecorder : ITaskWriteRecorder, IDisposable
{
    /// <summary>The records written in one change of the record.</summary>
    internal const int BatchSize = 100;

    /// <summary>How long a record may stay buffered before <see cref="FlushIfDueAsync"/> writes it.</summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly ITaskWriteRecordStore? _store;
    private readonly string _taskId;
    private readonly DatasetKey _dataset;
    private readonly string _toolId;
    private readonly object _lock = new();
    private readonly List<PendingWrite> _pending = [];
    private readonly List<(DatasetKey Dataset, string Path, string Stamp)> _stamps = [];
    private readonly SemaphoreSlim _flush = new(1, 1);
    private long _oldestPending;

    private readonly TaskEntryChanges? _changes;
    private readonly IReadOnlyList<string> _sourcesAfterCommit;

    private TaskWriteRecorder(ITaskWriteRecordStore? store, string taskId, DatasetKey dataset, string toolId,
        TaskEntryChanges? changes = null, IReadOnlyList<string>? sourcesAfterCommit = null)
    {
        _changes = changes;
        _store = store;
        _taskId = taskId;
        _dataset = dataset;
        _toolId = toolId;
        _sourcesAfterCommit = sourcesAfterCommit ?? [];
    }

    /// <summary>
    /// The recorder of a tool with <see cref="Ports.IHeavyTool.AddsDatasetFiles"/>; <paramref name="sourcesAfterCommit"/>
    /// (<see cref="Ports.IHeavyTool.SourcesDeletedAfterCommit"/>, in the task's dataset) are written with the commit.
    /// </summary>
    public static TaskWriteRecorder For(ITaskWriteRecordStore store, string taskId, DatasetKey dataset, string toolId,
        TaskEntryChanges? changes = null, IReadOnlyList<string>? sourcesAfterCommit = null) =>
        new(store ?? throw new ArgumentNullException(nameof(store)), taskId, dataset, toolId, changes,
            sourcesAfterCommit);

    /// <summary>The sources the task deletes after its commit, as the commit writes them (null: none).</summary>
    public TaskCommittedSources? CommittedSources => _sourcesAfterCommit.Count == 0
        ? null
        : new TaskCommittedSources(_dataset.OrgSlug, _dataset.InternalRef, _sourcesAfterCommit);

    /// <summary>The recorder of a tool without it: it refuses every record.</summary>
    public static TaskWriteRecorder Disabled(string taskId, DatasetKey dataset, string toolId) =>
        new(null, taskId, dataset, toolId);

    public bool Enabled => _store != null;

    public void Record(string path) => Add(_dataset, path, false);

    public void Record(DatasetKey dataset, string path) => Add(dataset, path, false);

    public void RecordFolder(string path) => Add(_dataset, path, true);

    public void RecordFolder(DatasetKey dataset, string path) => Add(dataset, path, true);

    public void Written(DatasetKey dataset, string path, string localPath)
    {
        Store();
        if (TaskWriteRecord.StampOf(localPath) is not { } stamp)
            return;

        lock (_lock)
        {
            if (_pending.Count == 0 && _stamps.Count == 0)
                _oldestPending = Stopwatch.GetTimestamp();
            _stamps.Add((dataset, path, stamp));
        }
    }

    /// <summary>The records not written yet: the paths and folders, then the stamps.</summary>
    public IReadOnlyList<PendingWrite> Pending
    {
        get
        {
            lock (_lock)
                return [.. _pending, .. _stamps.Select(s => new PendingWrite(s.Dataset, s.Path, false, s.Stamp))];
        }
    }

    public ValueTask FlushIfDueAsync(CancellationToken ct = default)
    {
        bool due;
        lock (_lock)
            due = _pending.Count + _stamps.Count >= BatchSize ||
                  (_pending.Count + _stamps.Count > 0 && Stopwatch.GetElapsedTime(_oldestPending) >= FlushInterval);
        return due ? new ValueTask(FlushAsync(ct)) : ValueTask.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_store is null)
            return;

        await _flush.WaitAsync(ct);
        try
        {
            while (true)
            {
                PendingWrite[] batch;
                (DatasetKey Dataset, string Path, string Stamp)[] stamps;
                lock (_lock)
                {
                    batch = [.. _pending.Take(BatchSize)];
                    // The stamps of the paths recorded before (or with) this batch
                    stamps = batch.Length < BatchSize ? [.. _stamps] : [];
                }

                if (batch.Length == 0 && stamps.Length == 0)
                    return;

                foreach (var dataset in batch.Select(p => p.Dataset).Concat(stamps.Select(s => s.Dataset)).Distinct())
                    await _store.RecordAsync(_taskId, dataset,
                        [.. batch.Where(p => p.Dataset == dataset && !p.IsFolder).Select(p => p.Path)],
                        [.. batch.Where(p => p.Dataset == dataset && p.IsFolder).Select(p => p.Path)], ct,
                        stamps.Where(s => s.Dataset == dataset)
                            .GroupBy(s => s.Path).ToDictionary(g => g.Key, g => g.Last().Stamp));

                // Removed once written: a failed write leaves them for the rollback
                lock (_lock)
                {
                    _pending.RemoveRange(0, batch.Length);
                    _stamps.RemoveRange(0, stamps.Length);
                    _oldestPending = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            _flush.Release();
        }
    }

    public async Task MarkCommittedAsync(CancellationToken ct = default)
    {
        var store = Store();
        await FlushAsync(ct);
        if (!await store.MarkCommittedAsync(_taskId, ct, CommittedSources))
            throw TaskWriteRecordClosedException.Cancelled(_taskId);
    }

    public Task CreatedDatasetAsync(TaskCreatedDataset dataset, CancellationToken ct = default) =>
        Store().SetCreatedDatasetAsync(_taskId, dataset, ct);

    public async Task<TaskCreatedDataset?> RecordedCreatedDatasetAsync(CancellationToken ct = default) =>
        (await Store().ReadAsync(_taskId, ct))?.CreatedDataset;

    public async Task ForgetAsync(DatasetKey dataset, string path, CancellationToken ct = default)
    {
        var store = Store();
        bool pending;
        lock (_lock)
        {
            pending = _pending.RemoveAll(p => p.Dataset == dataset && !p.IsFolder && p.Path == path) > 0;
            // A stamp taken before a move that then failed: its flush would record the path again
            _stamps.RemoveAll(s => s.Dataset == dataset && s.Path == path);
        }

        // Written already (saved before the write, as it must be): dropped from the stored record
        if (!pending)
            await store.ForgetAsync(_taskId, dataset, path, ct);
    }

    public async Task<IReadOnlySet<string>> RecordedPathsAsync(DatasetKey dataset, CancellationToken ct = default)
    {
        var record = await Store().ReadAsync(_taskId, ct);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (record?.Datasets.FirstOrDefault(d => d.InternalRef == dataset.InternalRef && d.Org == dataset.OrgSlug) is
            { } written)
            paths.UnionWith(written.Paths);
        lock (_lock)
            paths.UnionWith(_pending.Where(p => p.Dataset == dataset && !p.IsFolder).Select(p => p.Path));
        return paths;
    }

    public Task EntriesLeftAsync(DatasetKey dataset, IReadOnlyCollection<string> paths) =>
        _changes is null ? Task.CompletedTask : _changes.EntriesLeftAsync(dataset, paths);

    private void Add(DatasetKey dataset, string path, bool isFolder)
    {
        Store();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A recorded path cannot be empty", nameof(path));

        lock (_lock)
        {
            if (_pending.Count == 0)
                _oldestPending = Stopwatch.GetTimestamp();
            _pending.Add(new PendingWrite(dataset, path, isFolder));
        }
    }

    public void Dispose() => _flush.Dispose();

    private ITaskWriteRecordStore Store() =>
        _store ?? throw new InvalidOperationException(
            $"Tool '{_toolId}' records the files it adds, but does not declare AddsDatasetFiles");
}
