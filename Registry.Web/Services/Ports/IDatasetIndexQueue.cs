#nullable enable
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
/// coalesced into a single native batch (<see cref="IDdbIndex.AddRawBatchWithOptions"/>), which
/// keeps the underlying SQLite write transaction short and gives callers FIFO fairness.
/// </summary>
public interface IDatasetIndexQueue
{
    /// <summary>
    /// Enqueues a dataset-relative path and completes once it has been committed to the index
    /// (or found unchanged, or failed). Multiple concurrent calls for the same dataset are
    /// coalesced into a single native batch call.
    /// </summary>
    Task<Entry> EnqueueAsync(DatasetKey dataset, string path, CancellationToken ct = default);

    /// <summary>Enqueues several paths as one logical unit.</summary>
    Task<IReadOnlyList<Entry>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<string> paths,
        CancellationToken ct = default);

    /// <summary>
    /// Enqueues several files as one logical unit, with the hashes computed while they were
    /// written, on behalf of an upload started with <see cref="BeginUpload"/> (or none). Each
    /// file gets its own outcome: one that fails does not fail the others.
    /// </summary>
    Task<IReadOnlyList<IndexOutcome>> EnqueueAsync(DatasetKey dataset, IReadOnlyList<AddItem> items,
        UploadTicket? upload, CancellationToken ct = default);

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
/// An upload registered with <see cref="IDatasetIndexQueue.BeginUpload"/>. Ends (once) when its
/// files are enqueued or when it is disposed.
/// </summary>
public abstract class UploadTicket : IDisposable
{
    private int _ended;

    /// <summary>Whether the upload ended (its files were enqueued or it was disposed).</summary>
    public bool Ended => Volatile.Read(ref _ended) != 0;

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
