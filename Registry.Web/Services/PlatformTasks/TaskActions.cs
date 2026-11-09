using System.Collections.Generic;
using Hangfire.States;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>
/// What a task row offers its reader (D-024, FR-XC-15): the Registry decides it from the task's state
/// (<see cref="TaskStateCatalog"/>) and the caller's rights (<see cref="TaskAuthorizer"/>), and the task summaries carry
/// it as <c>actions</c>. The Hub renders these buttons and no others.
/// </summary>
public static class TaskActions
{
    public const string Cancel = "cancel";
    public const string Retry = "retry";
    public const string Delete = "delete";
    public const string ViewLog = "viewLog";

    /// <summary>
    /// The actions on a task in <paramref name="state"/>. A caller who may touch the task (its owner, the dataset's
    /// owner, an admin, or anybody for a system task) reads its log (D-033); with write access to the dataset too, it
    /// cancels an active task, retries a failed one (only a failed one, D-037: a cancelled task is started again from
    /// its tool) and deletes an ended one. A task that committed its result (FR-XC-27: its write record is committed)
    /// offers neither Cancel nor Retry: its cancel answers 400 (D-002) and its retry 409 (D-037).
    /// </summary>
    /// <param name="state">The task's state.</param>
    /// <param name="mayTouch">The caller may touch the task (<see cref="TaskCaller.MayTouch"/>).</param>
    /// <param name="canWrite">The caller has write access to the task's dataset.</param>
    /// <param name="committed">The task committed its result (<see cref="TaskWriteRecord.IsCommitted"/>).</param>
    /// <returns>The actions, in the order the row shows them.</returns>
    public static IReadOnlyList<string> For(string state, bool mayTouch, bool canWrite, bool committed = false)
    {
        if (!mayTouch)
            return [];
        if (!canWrite)
            return [ViewLog];

        if (TaskStateCatalog.IsTerminal(state))
            return state == FailedState.StateName && !committed ? [ViewLog, Retry, Delete] : [ViewLog, Delete];

        return committed ? [ViewLog] : [ViewLog, Cancel];
    }
}

/// <summary>
/// Who reads a dataset's tasks: the user (null when anonymous), whether they own the dataset or are an admin, and
/// whether they may write to it. Asked once per request (<see cref="TaskAuthorizer.CallerAsync"/>).
/// </summary>
public sealed record TaskCaller(string? UserId, bool OwnsDataset, bool CanWrite)
{
    /// <summary>The ownership rule of <see cref="TaskAuthorizer.LoadAsync"/>: a system task, the task's owner, the
    /// dataset's owner or an admin.</summary>
    /// <param name="taskUserId">The user the task belongs to (null: a system task).</param>
    public bool MayTouch(string? taskUserId) =>
        taskUserId is null || (UserId is not null && taskUserId == UserId) || OwnsDataset;

    /// <summary>The actions of a task (<see cref="TaskActions.For"/>).</summary>
    public IReadOnlyList<string> ActionsOn(string state, string? taskUserId, bool committed = false) =>
        TaskActions.For(state, MayTouch(taskUserId), CanWrite, committed);
}
