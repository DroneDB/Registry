using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>
/// An in-memory <see cref="ITaskWriteRecorder"/>: what a tool records (files and folders, in order, with the file a
/// stamp was taken from), forgets and commits, readable by a test. Records are kept as they are made (no batching).
/// </summary>
public sealed class FakeTaskWriteRecorder : ITaskWriteRecorder
{
    private readonly object _lock = new();
    private readonly List<string> _paths = [];
    private readonly List<string> _folders = [];
    private readonly Dictionary<string, string> _stamped = new(StringComparer.Ordinal);
    private readonly List<string> _forgotten = [];
    private readonly List<string> _entriesLeft = [];
    private readonly List<string> _savedPaths = [];
    private readonly List<string> _savedFolders = [];

    /// <summary>The paths of an earlier attempt of the task (<see cref="RecordedPathsAsync"/>).</summary>
    public HashSet<string> Earlier { get; } = new(StringComparer.Ordinal);

    /// <summary>The commit is refused, as after a cancel request.</summary>
    public bool RefuseCommit { get; set; }

    /// <summary>Called when the record is committed (after the refusal check).</summary>
    public Action? OnCommit { get; set; }

    /// <summary>Called for every file recorded, with its path.</summary>
    public Action<string>? OnRecord { get; set; }

    public bool Committed { get; private set; }

    public int Flushes { get; private set; }

    /// <summary>The files recorded, in order (a forgotten one is removed).</summary>
    public IReadOnlyList<string> Paths
    {
        get { lock (_lock) return [.. _paths]; }
    }

    /// <summary>The folders recorded, in order.</summary>
    public IReadOnlyList<string> Folders
    {
        get { lock (_lock) return [.. _folders]; }
    }

    /// <summary>The paths stamped, with the local file the stamp was taken from.</summary>
    public IReadOnlyDictionary<string, string> Stamped
    {
        get { lock (_lock) return new Dictionary<string, string>(_stamped); }
    }

    /// <summary>
    /// The files in the record as last saved (<see cref="FlushAsync"/>): a tool writes a path only once it is here
    /// (§8.1).
    /// </summary>
    public IReadOnlyList<string> SavedPaths
    {
        get { lock (_lock) return [.. _savedPaths]; }
    }

    /// <summary>The folders in the record as last saved.</summary>
    public IReadOnlyList<string> SavedFolders
    {
        get { lock (_lock) return [.. _savedFolders]; }
    }

    public IReadOnlyList<string> Forgotten
    {
        get { lock (_lock) return [.. _forgotten]; }
    }

    public IReadOnlyList<string> EntriesLeft
    {
        get { lock (_lock) return [.. _entriesLeft]; }
    }

    public bool Enabled => true;

    public void Record(string path) => Add(path);

    public void Record(DatasetKey dataset, string path) => Add(path);

    public void RecordFolder(string path)
    {
        lock (_lock)
            _folders.Add(path);
    }

    public void RecordFolder(DatasetKey dataset, string path) => RecordFolder(path);

    public void Written(DatasetKey dataset, string path, string localPath)
    {
        lock (_lock)
            _stamped[path] = localPath;
    }

    public ValueTask FlushIfDueAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public Task FlushAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            Flushes++;
            _savedPaths.Clear();
            _savedPaths.AddRange(_paths);
            _savedFolders.Clear();
            _savedFolders.AddRange(_folders);
        }

        return Task.CompletedTask;
    }

    public Task MarkCommittedAsync(CancellationToken ct = default)
    {
        if (RefuseCommit)
            throw TaskWriteRecordClosedException.Cancelled("task-1");
        Committed = true;
        OnCommit?.Invoke();
        return Task.CompletedTask;
    }

    public Task CreatedDatasetAsync(TaskCreatedDataset dataset, CancellationToken ct = default) => Task.CompletedTask;

    public Task ForgetAsync(DatasetKey dataset, string path, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _paths.Remove(path);
            _savedPaths.Remove(path);
            _forgotten.Add(path);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> RecordedPathsAsync(DatasetKey dataset, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(_paths.Concat(Earlier),
                StringComparer.Ordinal));
    }

    public Task EntriesLeftAsync(DatasetKey dataset, IReadOnlyCollection<string> paths)
    {
        lock (_lock)
            _entriesLeft.AddRange(paths);
        return Task.CompletedTask;
    }

    private void Add(string path)
    {
        lock (_lock)
            _paths.Add(path);
        OnRecord?.Invoke(path);
    }
}
