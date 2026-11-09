using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Ports;

/// <summary>The outcome of a change of a write record (<see cref="ITaskWriteRecordStore.ChangeAsync"/>).</summary>
/// <param name="Record">The record after the change (or as it is, when the change was refused); null when there is none.</param>
/// <param name="Changed">Whether the change was written.</param>
/// <param name="TaskFound">Whether the task's row exists.</param>
public readonly record struct TaskWriteRecordChange(TaskWriteRecord? Record, bool Changed, bool TaskFound);

/// <summary>What a cancel request found in the write record (<see cref="ITaskWriteRecordStore.RequestCancelAsync"/>).</summary>
public enum TaskCancelRequestResult
{
    /// <summary>The task has no write record (a tool that adds no dataset files, or a task queued before them).</summary>
    NoRecord,

    /// <summary>The record is now (or was already) cancelRequested: the task can no longer commit.</summary>
    Requested,

    /// <summary>The task committed its output: it cannot be cancelled (D-002, FR-XC-27).</summary>
    Committed,

    /// <summary>The record is being or was rolled back: the task has ended.</summary>
    RolledBack
}

/// <summary>
/// A write or a commit was refused: a rollback claimed the record, or (for a commit) a cancel was requested. The task
/// must stop without writing or deleting anything more.
/// </summary>
public sealed class TaskWriteRecordClosedException(string message) : InvalidOperationException(message)
{
    /// <summary>A rollback claimed the record of <paramref name="taskId"/>.</summary>
    public static TaskWriteRecordClosedException RollingBack(string taskId) =>
        new($"Task {taskId} is being rolled back: it can write nothing more.");

    /// <summary>The commit of <paramref name="taskId"/> came after a cancel request.</summary>
    public static TaskWriteRecordClosedException Cancelled(string taskId) =>
        new($"Task {taskId} was cancelled before it could commit its output.");
}

/// <summary>
/// The write record of a task (<c>JobIndex.WriteRecordJson</c>, FR-XC-27, D-045). Every change is a compare-and-swap on
/// the whole stored string (<c>UPDATE … WHERE JobId = @id AND WriteRecordJson = @old</c>, or <c>IS NULL</c> for the first
/// write), retried when another writer changed it first, so a commit and a cancel never both win.
/// </summary>
public interface ITaskWriteRecordStore
{
    /// <summary>The record of <paramref name="taskId"/>; null when it has none (or the task is unknown).</summary>
    Task<TaskWriteRecord?> ReadAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="change"/> to a fresh copy of the stored record (null: none) and writes its result by
    /// compare-and-swap, again on a fresh copy when another writer changed the record first. A change that returns null
    /// writes nothing.
    /// </summary>
    Task<TaskWriteRecordChange> ChangeAsync(string taskId, Func<TaskWriteRecord?, TaskWriteRecord?> change,
        CancellationToken ct = default);

    /// <summary>Appends paths and folders the task is about to create in <paramref name="dataset"/>.</summary>
    /// <exception cref="TaskWriteRecordClosedException">A rollback claimed the record.</exception>
    Task RecordAsync(string taskId, DatasetKey dataset, IReadOnlyCollection<string> paths,
        IReadOnlyCollection<string> folders, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? stamps = null);

    /// <summary>Records the dataset the task created (photogrammetry creates it in the worker).</summary>
    /// <exception cref="TaskWriteRecordClosedException">A rollback claimed the record.</exception>
    Task SetCreatedDatasetAsync(string taskId, TaskCreatedDataset created, CancellationToken ct = default);

    /// <summary>open → committed (already committed: true); false when a cancel was requested or a rollback claimed it.</summary>
    Task<bool> MarkCommittedAsync(string taskId, CancellationToken ct = default);

    /// <summary>open → cancelRequested; refused (<see cref="TaskCancelRequestResult.Committed"/>) for a committed record.</summary>
    Task<TaskCancelRequestResult> RequestCancelAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// open, cancelRequested or rollingBack (a rollback that stopped half-way is resumed) → rollingBack: the claimed
    /// record; null when there is none, or it is committed or rolled back.
    /// </summary>
    Task<TaskWriteRecord?> ClaimRollbackAsync(string taskId, CancellationToken ct = default);

    /// <summary>rollingBack → rolledBack.</summary>
    Task MarkRolledBackAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// Drops <paramref name="path"/> of <paramref name="dataset"/> from the record: the task recorded it but could not
    /// create it (another writer created it meanwhile), so it is not the task's and a rollback must not delete it.
    /// </summary>
    Task ForgetAsync(string taskId, DatasetKey dataset, string path, CancellationToken ct = default);

    /// <summary>Removes the record.</summary>
    Task ClearAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// The ended (Failed or Deleted) tasks whose record still needs its rollback (open, cancelRequested or rollingBack):
    /// the startup sweep finishes them.
    /// </summary>
    Task<IReadOnlyList<string>> EndedWithOpenRecordAsync(CancellationToken ct = default);

    /// <summary>
    /// The paths of <paramref name="dataset"/> held by the record of a task that did not succeed and whose record is not
    /// committed nor rolled back (it runs, or its rollback has not finished): the reconciliation sweep leaves them alone.
    /// </summary>
    Task<IReadOnlySet<string>> HeldPathsAsync(DatasetKey dataset, CancellationToken ct = default);
}
