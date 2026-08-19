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
/// keeps the underlying SQLite write transaction short and gives callers FIFO fairness
/// FIFO fairness.
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
    /// Releases the per-dataset lane so idle memory is not retained for a removed dataset.
    /// In-flight requests already written to the lane keep committing (the drain loop
    /// processes the remainder before retiring); enqueues after the release transparently
    /// recreate a fresh lane unless the dataset is truly gone.
    /// </summary>
    void Release(DatasetKey dataset);
}
