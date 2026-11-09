using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Ports;

/// <summary>What a rollback did (<see cref="ITaskRollback.RollBackAsync"/>).</summary>
public enum TaskRollbackOutcome
{
    /// <summary>The task had nothing to roll back (no record, or it was rolled back already).</summary>
    Nothing,

    /// <summary>The task is committed: it is never rolled back (FR-XC-27).</summary>
    Committed,

    /// <summary>Everything the record held is gone (a dataset the task created is deleted a few minutes later).</summary>
    RolledBack,

    /// <summary>Something could not be deleted: the record stays claimed, and the startup sweep tries again.</summary>
    Incomplete
}

/// <summary>How an attempt of a task starts with its write record (<see cref="ITaskRollback.StartAttemptAsync"/>).</summary>
public enum TaskAttemptStart
{
    /// <summary>No path recorded yet: the attempt starts with an empty record (a created dataset is kept in it).</summary>
    Fresh,

    /// <summary>An automatic retry: the record of the earlier attempts is kept.</summary>
    Retry,

    /// <summary>The earlier run of this attempt was lost (a restart): what it recorded was deleted, and the attempt starts empty.</summary>
    LostRunRolledBack,

    /// <summary>
    /// An earlier run committed (it was lost by a restart, or failed after its commit): the task must not run again (it
    /// fails, its output stays).
    /// </summary>
    CommittedLost
}

/// <summary>
/// The one rollback of a task that adds dataset files (FR-XC-27, D-004, D-045): it deletes only what the task's write
/// record holds (the paths and then the empty folders it created, and a dataset it created, by a delayed system delete
/// when nothing else is in it), never a committed task, and can run twice.
/// </summary>
public interface ITaskRollback
{
    /// <summary>
    /// Rolls back a cancelled, finally failed or lost task: claims its record, deletes what it holds and
    /// <paramref name="pending"/> (what the worker recorded and had not written yet), and marks it rolled back.
    /// </summary>
    Task<TaskRollbackOutcome> RollBackAsync(string taskId, IReadOnlyCollection<PendingWrite>? pending = null,
        CancellationToken ct = default);

    /// <summary>
    /// The attempt rule, when an attempt of the task starts (<paramref name="retryCount"/>: Hangfire's retry count): a
    /// record of this same attempt that holds writes belongs to a run a restart lost, which is rolled back (the dataset
    /// the task created is kept: the job runs again into it) before the attempt starts with an empty record; a record
    /// of an earlier attempt is an automatic retry's, kept.
    /// </summary>
    Task<TaskAttemptStart> StartAttemptAsync(string taskId, int retryCount, CancellationToken ct = default);

    /// <summary>
    /// The startup sweep: rolls back every ended (Failed or Deleted) task whose record still needs it (a cancel of a task
    /// that never started, a rollback a stop interrupted, a job the storage lost), and schedules again the delayed
    /// delete of a dataset a rolled-back task created whose job the storage lost (InMemory storage). The number of tasks
    /// rolled back.
    /// </summary>
    Task<int> SweepAsync(CancellationToken ct = default);

    /// <summary>
    /// Why a committed task that was lost (its job gone with the job storage, so it never runs again) ends Failed: the
    /// sources it deletes after its commit (<see cref="TaskWriteRecord.CommittedSources"/>) that remain. Null when its
    /// record is not committed.
    /// </summary>
    Task<TaskCommittedLostException?> CommittedLostAsync(string taskId, CancellationToken ct = default);
}

/// <summary>
/// The earlier run of a task was lost after it committed its output: the task does not run again (FR-XC-27). It ends
/// Failed with this message, which names the sources it was to delete after the commit and that remain.
/// </summary>
public sealed class TaskCommittedLostException : InvalidOperationException
{
    private const string Stopped =
        "The task stopped after it published its output and does not run again: its output is kept";

    /// <summary>How many remaining sources the message names.</summary>
    internal const int NamedSources = 10;

    /// <summary>A task that deletes no source after its commit.</summary>
    public TaskCommittedLostException() : base(Stopped + ".")
    {
    }

    /// <summary>A task that deletes sources after its commit: <paramref name="remainingSources"/> are still there.</summary>
    public TaskCommittedLostException(IReadOnlyList<string> remainingSources) : base(MessageFor(remainingSources))
    {
        RemainingSources = remainingSources;
    }

    /// <summary>The sources the task was to delete after its commit that remain.</summary>
    public IReadOnlyList<string> RemainingSources { get; } = [];

    /// <summary>
    /// The exception of a task that deletes <paramref name="sources"/> after its commit (none: the message names none),
    /// naming those that <paramref name="remains"/>: the one message of the worker's attempt rule and of the end of a
    /// lost task.
    /// </summary>
    public static TaskCommittedLostException For(IReadOnlyList<string> sources, Func<string, bool> remains)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(remains);
        return sources.Count == 0 ? new TaskCommittedLostException() : new TaskCommittedLostException([.. sources.Where(remains)]);
    }

    private static string MessageFor(IReadOnlyList<string> remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        if (remaining.Count == 0)
            return Stopped + ", and the sources it was to delete are gone.";

        var named = string.Join(", ", remaining.Take(NamedSources).Select(p => $"'{p}'"));
        var more = remaining.Count > NamedSources ? $" and {remaining.Count - NamedSources} more" : "";
        return Stopped + $", and the sources it had not deleted yet remain: {named}{more}.";
    }
}
