using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>Identifies a dataset for indexing purposes.</summary>
public readonly record struct DatasetKey(string OrgSlug, Guid InternalRef);

/// <summary>
/// Schedules index writes for a dataset. Concurrent requests for the same dataset are
/// coalesced into a single native batch (<see cref="IDdbIndex.AddRawBatchWithOptions(System.Collections.Generic.IReadOnlyList{AddItem},bool)"/>), which
/// keeps the underlying SQLite write transaction short and gives callers FIFO fairness.
/// </summary>
public interface IDatasetIndexQueue
{
    /// <summary>Enqueues several paths as one logical unit.</summary>
    Task<IReadOnlyList<Entry>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<string> paths,
        CancellationToken ct = default);

    /// <summary>
    /// Enqueues several files as one logical unit, with the hashes computed while they were
    /// written, on behalf of an upload started with <see cref="BeginUpload"/> (or none). Each
    /// file gets its own outcome: one that fails does not fail the others. At the deadline and at the shutdown of
    /// the queue too: a file no batch took is never indexed (a transient error), one a batch is still writing is
    /// <c>IndexWritePendingException</c>; only a cancellation through <paramref name="ct"/> throws.
    /// </summary>
    /// <param name="late">Told the final outcome of each item reported as <c>IndexWritePendingException</c>, once
    /// the batch that holds it ends (also when the queue is shutting down); never called for any other item.</param>
    Task<IReadOnlyList<IndexOutcome>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<AddItem> items,
        UploadTicket? upload, LateIndexOutcome? late = null, CancellationToken ct = default);

    /// <summary>
    /// Registers an upload whose files will be enqueued for <paramref name="dataset"/> once they
    /// are on disk. While uploads are registered the lane keeps its batch window open for them;
    /// once every registered upload has enqueued, a batch of their files commits at once instead
    /// of waiting for the window (a single uploader never waits). Dispose the ticket when the
    /// request ends, enqueued or not.
    /// </summary>
    UploadTicket BeginUpload(DatasetKey dataset);

    /// <summary>
    /// Releases the per-dataset lane so idle memory is not retained for a removed dataset.
    /// In-flight requests already written to the lane keep committing (the drain loop
    /// processes the remainder before retiring); enqueues after the release transparently
    /// recreate a fresh lane unless the dataset is truly gone.
    /// </summary>
    void Release(DatasetKey dataset);
}

/// <summary>The index outcome of one file: its entry, or why it was not indexed.</summary>
public readonly record struct IndexOutcome(Entry? Entry, Exception? Error);

/// <summary>
/// The final outcome of item <paramref name="item"/> (its position in the enqueued list) whose caller stopped
/// waiting while a batch was writing it. Called once, by the queue, right after that batch and before the next batch of
/// the dataset starts: no write of the queue to that index runs while the callee acts on the outcome. Must not
/// throw (a failure is logged) and must not wait on the queue.
/// </summary>
public delegate void LateIndexOutcome(int item, IndexOutcome outcome);

/// <summary>
/// An upload registered with <see cref="IDatasetIndexQueue.BeginUpload"/>. Ends (once) when its
/// files are enqueued or when it is disposed.
/// </summary>
public abstract class UploadTicket : IDisposable
{
    private int _ended;

    /// <summary>Ends the upload; returns false when it had already ended.</summary>
    public bool End()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return false;
        OnEnded();
        return true;
    }

    protected abstract void OnEnded();

    public void Dispose()
    {
        End();
        GC.SuppressFinalize(this);
    }
}
