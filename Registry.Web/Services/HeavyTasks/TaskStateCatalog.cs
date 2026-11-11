using System;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// Canonical, server-authoritative catalog of Processing Platform task states.
/// Single source of truth shared by the task substrate (active/terminal
/// classification in the query layer and controller) and surfaced to clients via
/// the features payload, so the UI no longer hardcodes the state machine.
/// </summary>
public static class TaskStateCatalog
{
    /// <summary>
    /// Non-terminal states a task can be in while still running. "Indexing" is the initial state
    /// of an indexed job until its publication completes (IndexingState.StateName, kept as a
    /// literal so this catalog does not depend on the Hangfire adapters).
    /// </summary>
    public static readonly string[] Active = ["Created", "Indexing", "Enqueued", "Scheduled", "Processing"];

    /// <summary>Terminal states a task ends in.</summary>
    public static readonly string[] Terminal = ["Succeeded", "Failed", "Deleted"];

    /// <summary>All states in lifecycle order (active first, then terminal).</summary>
    public static readonly string[] All =
        ["Created", "Indexing", "Enqueued", "Scheduled", "Processing", "Succeeded", "Failed", "Deleted"];

    public static bool IsTerminal(string state) => Array.IndexOf(Terminal, state) >= 0;
}
