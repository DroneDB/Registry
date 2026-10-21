#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The libddb operation ids of the running jobs (XI-08): one per job, mapped to the job id and
/// its dataset, so a native log record (<see cref="Adapters.DdbLogSink"/>) says which job and
/// dataset it belongs to. Process-wide like libddb's log callback: ids are unique in the process.
/// </summary>
public static class JobOperations
{
    private static readonly ConcurrentDictionary<ulong, JobOperation> Running = new();
    private static long _lastId;

    /// <summary>
    /// Starts the operation of job <paramref name="jobId"/> and makes it the current one
    /// (<see cref="DdbOperation"/>) until it is disposed. Begin and dispose in the same flow.
    /// </summary>
    /// <param name="jobId">The Hangfire job id.</param>
    /// <param name="dataset">"org/dataset" of the job, or null when it has none.</param>
    public static JobOperation Begin(string jobId, string? dataset)
    {
        var id = (ulong)Interlocked.Increment(ref _lastId);
        var operation = new JobOperation(id, jobId, dataset, DdbOperation.Enter(id));
        Running[id] = operation;
        return operation;
    }

    /// <summary>The running operation <paramref name="id"/>, or null.</summary>
    public static JobOperation? Find(ulong id) => id != 0 && Running.TryGetValue(id, out var op) ? op : null;

    /// <summary>The operation of the current flow, or null.</summary>
    public static JobOperation? Current => Find(DdbOperation.Current);

    internal static void End(JobOperation operation) => Running.TryRemove(operation.Id, out _);
}

/// <summary>
/// One running job's libddb operation: its id, job and dataset, and the native warnings waiting
/// for the job's thread to write them to the job console (a native thread must not: the console
/// shares the job's storage connection).
/// </summary>
public sealed class JobOperation : IDisposable
{
    /// <summary>Native lines kept for the console at most; later ones are counted, not kept.</summary>
    public const int MaxQueuedLines = 100;

    private readonly ConcurrentQueue<string> _nativeLines = new();
    private readonly DdbOperation.Scope _scope;
    private int _queued;
    private int _dropped;
    private int _disposed;

    internal JobOperation(ulong id, string jobId, string? dataset, DdbOperation.Scope scope)
    {
        Id = id;
        JobId = jobId;
        Dataset = dataset;
        _scope = scope;
    }

    public ulong Id { get; }
    public string JobId { get; }
    public string? Dataset { get; }

    /// <summary>Queues a native line for the job console. Thread-safe, never blocks.</summary>
    public void QueueNativeLine(string line)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueuedLines)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _nativeLines.Enqueue(line);
    }

    /// <summary>The queued native lines (and a note of the dropped ones), oldest first.</summary>
    public IEnumerable<string> TakeNativeLines()
    {
        while (_nativeLines.TryDequeue(out var line))
            yield return line;

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            yield return $"({dropped} more native messages in the server log)";
    }

    /// <summary>Ends the operation and restores the previous current one.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        JobOperations.End(this);
        _scope.Dispose();
    }
}
