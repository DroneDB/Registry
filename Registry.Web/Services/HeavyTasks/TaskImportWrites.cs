using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.Import;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// The <see cref="IImportWriteRecord"/> of a task over its write record (<see cref="ITaskWriteRecorder"/>) in one
/// dataset: the one way the import sources and the archive extraction record what they create (FR-XC-27). It keeps
/// the files the task created, in order, which the tool then indexes (<see cref="Written"/>): never the other files
/// of the folder.
/// </summary>
public sealed class TaskImportWrites : IImportWriteRecord
{
    private readonly ITaskWriteRecorder _recorder;
    private readonly DatasetKey _dataset;
    private readonly string _root;
    private readonly IReadOnlySet<string> _earlier;
    private readonly object _lock = new();
    private readonly List<string> _written = [];
    private readonly HashSet<string> _writtenSet = new(StringComparer.Ordinal);

    private TaskImportWrites(ITaskWriteRecorder recorder, DatasetKey dataset, string root, IReadOnlySet<string> earlier)
    {
        _recorder = recorder;
        _dataset = dataset;
        _root = root;
        _earlier = earlier;
    }

    /// <summary>
    /// The writes of the task into <paramref name="dataset"/> (its folder <paramref name="root"/>); the paths an earlier
    /// attempt recorded are the task's own.
    /// </summary>
    public static async Task<TaskImportWrites> StartAsync(ITaskWriteRecorder recorder, DatasetKey dataset, string root,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return new TaskImportWrites(recorder, dataset, root, await recorder.RecordedPathsAsync(dataset, ct));
    }

    /// <summary>The files the task created, in the order it recorded them, each once.</summary>
    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_lock)
                return [.. _written];
        }
    }

    public bool MayWrite(string path)
    {
        if (_earlier.Contains(path))
            return true;
        var local = DatasetPathPolicy.ToLocalPath(_root, path);
        return !File.Exists(local) && !Directory.Exists(local);
    }

    public void Record(string path)
    {
        _recorder.Record(_dataset, path);
        lock (_lock)
            if (_writtenSet.Add(path))
                _written.Add(path);
    }

    public void RecordFolder(string path) => _recorder.RecordFolder(_dataset, path);

    public void Written(string path) => _recorder.Written(_dataset, path, DatasetPathPolicy.ToLocalPath(_root, path));

    public Task ForgetAsync(string path, CancellationToken ct)
    {
        lock (_lock)
            if (_writtenSet.Remove(path))
                _written.Remove(path);
        return _recorder.ForgetAsync(_dataset, path, ct);
    }

    public ValueTask FlushIfDueAsync(CancellationToken ct) => _recorder.FlushIfDueAsync(ct);

    /// <summary>
    /// Creates the folder <paramref name="folder"/> (dataset-relative) of the dataset and its missing parents, each
    /// recorded before it is created.
    /// </summary>
    public static void CreateFolders(IImportWriteRecord writes, string root, string folder)
    {
        var missing = new Stack<string>();
        for (var f = folder.Trim('/'); f.Length > 0 && !Directory.Exists(DatasetPathPolicy.ToLocalPath(root, f));
             f = f.Contains('/') ? f[..f.LastIndexOf('/')] : string.Empty)
            missing.Push(f);

        while (missing.Count > 0)
        {
            var f = missing.Pop();
            writes.RecordFolder(f);
            Directory.CreateDirectory(DatasetPathPolicy.ToLocalPath(root, f));
        }
    }

    /// <summary>The parent folder of a dataset path ("" at the root).</summary>
    public static string ParentOf(string path) =>
        path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
}
