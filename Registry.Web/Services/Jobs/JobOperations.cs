using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The libddb operation ids of the running jobs: one per job, mapped to the job id and
/// its dataset, so a native log record (<see cref="Adapters.DdbLogSink"/>) says which job and
/// dataset it belongs to. Process-wide like libddb's log callback: ids are unique in the process.
/// An operation also carries the job's cancellation: its native calls stop at their next
/// checkpoint when Hangfire cancels the job (server shutdown, job aborted).
/// </summary>
public static class JobOperations
{
    private static readonly ConcurrentDictionary<ulong, JobOperation> Running = new();

    /// <summary>
    /// Starts the operation of job <paramref name="jobId"/> and makes it the current one
    /// (<see cref="DdbOperation"/>), with <paramref name="cancellation"/> as the current
    /// <see cref="DdbCancellation"/>, until it is disposed. Begin and dispose in the same flow.
    /// </summary>
    /// <param name="jobId">The Hangfire job id.</param>
    /// <param name="dataset">"org/dataset" of the job, or null when it has none.</param>
    /// <param name="cancellation">The job's token (<see cref="JobCancellation.Of"/>).</param>
    public static JobOperation Begin(string jobId, string? dataset, CancellationToken cancellation = default)
    {
        var id = DdbOperation.NewId();
        var operation = new JobOperation(id, jobId, dataset, DdbOperation.Enter(id), DdbCancellation.Enter(cancellation));
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
    private readonly DdbCancellation.Scope _cancellation;
    private int _queued;
    private int _dropped;
    private int _disposed;

    internal JobOperation(ulong id, string jobId, string? dataset, DdbOperation.Scope scope,
        DdbCancellation.Scope cancellation)
    {
        Id = id;
        JobId = jobId;
        Dataset = dataset;
        _scope = scope;
        _cancellation = cancellation;
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

    /// <summary>Ends the operation and restores the previous current one (and cancellation).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        JobOperations.End(this);
        _cancellation.Dispose();
        _scope.Dispose();
    }
}
