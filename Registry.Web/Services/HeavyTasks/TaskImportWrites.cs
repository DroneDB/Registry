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
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// The <see cref="IImportWriteRecord"/> of a task over its write record (<see cref="ITaskWriteRecorder"/>) in one
/// dataset: the one way the import sources and the archive extraction record what they create (FR-XC-27). It keeps
/// the files the task created, in order, which the tool then indexes (<see cref="Created"/>): never the other files
/// of the folder. A path is in the stored record before the first byte of it is written (§8.1): a writer records a
/// batch of files and folders (<see cref="RecordFolders"/>, <see cref="Record"/>), saves them with one
/// <see cref="SaveAsync(IImportWriteRecord, CancellationToken)"/>, and only then creates them
/// (<see cref="CreateFolder"/>, a move that does not overwrite), so that a process that is lost after a write leaves a
/// record that rolls it back.
/// </summary>
public sealed class TaskImportWrites : IImportWriteRecord
{
    /// <summary>How many files a writer records and saves with one change of the record before it creates them.</summary>
    public const int SaveBatch = 100;

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
    public IReadOnlyList<string> Created
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

    public void Written(string path, string localFile) => _recorder.Written(_dataset, path, localFile);

    public Task ForgetAsync(string path, CancellationToken ct)
    {
        lock (_lock)
            if (_writtenSet.Remove(path))
                _written.Remove(path);
        return _recorder.ForgetAsync(_dataset, path, ct);
    }

    public ValueTask FlushIfDueAsync(CancellationToken ct) => _recorder.FlushIfDueAsync(ct);

    /// <summary>Saves what was recorded (and stamped) to the task's write record now: call it before creating it.</summary>
    /// <exception cref="Registry.Web.Services.Ports.TaskWriteRecordClosedException">A rollback claimed the record.</exception>
    public Task SaveAsync(CancellationToken ct) => _recorder.FlushAsync(ct);

    /// <summary>
    /// Saves what <paramref name="writes"/> recorded before anything of it is created (§8.1). Only the task's own write
    /// record (<see cref="TaskImportWrites"/>) can: any other is refused, since writing before the save could leave a
    /// file that no rollback finds.
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="writes"/> is not a <see cref="TaskImportWrites"/>.</exception>
    public static Task SaveAsync(IImportWriteRecord writes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);
        return writes is TaskImportWrites own
            ? own.SaveAsync(ct)
            : throw new NotSupportedException(
                $"{writes.GetType().Name} cannot save its records before the files are written");
    }

    /// <summary>
    /// Records the missing folders of <paramref name="folder"/> (dataset-relative; the missing parents first), which
    /// <see cref="CreateFolder"/> creates once the record is saved; those in <paramref name="recorded"/> (the folders
    /// of a batch recorded already) are not recorded again, and the others are added to it. The folders recorded.
    /// </summary>
    public static IReadOnlyList<string> RecordFolders(IImportWriteRecord writes, string root, string folder,
        ISet<string>? recorded = null)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var missing = new Stack<string>();
        for (var f = folder.Trim('/'); f.Length > 0 && !Directory.Exists(DatasetPathPolicy.ToLocalPath(root, f));
             f = f.Contains('/') ? f[..f.LastIndexOf('/')] : string.Empty)
            missing.Push(f);

        var added = new List<string>(missing.Count);
        while (missing.Count > 0)
        {
            var f = missing.Pop();
            if (recorded != null && !recorded.Add(f))
                continue;
            writes.RecordFolder(f);
            added.Add(f);
        }

        return added;
    }

    /// <summary>
    /// Creates the folder <paramref name="folder"/> (dataset-relative) and its missing parents, which
    /// <see cref="RecordFolders"/> recorded and the record saved.
    /// </summary>
    public static void CreateFolder(string root, string folder)
    {
        var f = folder.Trim('/');
        if (f.Length > 0)
            Directory.CreateDirectory(DatasetPathPolicy.ToLocalPath(root, f));
    }

    /// <summary>The parent folder of a dataset path ("" at the root).</summary>
    public static string ParentOf(string path) =>
        path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
}
