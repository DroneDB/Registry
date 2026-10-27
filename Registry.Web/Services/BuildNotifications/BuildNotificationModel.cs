#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>How a build ended, as far as an e-mail to its requester is concerned.</summary>
public enum BuildOutcome
{
    /// <summary>The entry is built.</summary>
    Completed,

    /// <summary>The build failed for good: no automatic retry is left.</summary>
    Failed
}

/// <summary>One finished build in a notification e-mail.</summary>
/// <param name="OrgSlug">The organization of the dataset.</param>
/// <param name="DsSlug">The dataset.</param>
/// <param name="Path">The entry that was built, relative to the dataset root.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">A short reason when it failed (no server paths in it); null otherwise.</param>
/// <param name="FinishedAtUtc">When it ended (UTC).</param>
public sealed record BuildNotificationLine(
    string OrgSlug,
    string DsSlug,
    string Path,
    BuildOutcome Outcome,
    string? Error,
    DateTime FinishedAtUtc)
{
    /// <summary>The dataset as the Hub names it: <c>org/ds</c>.</summary>
    public string Dataset => $"{OrgSlug}/{DsSlug}";
}

/// <summary>What a build notification e-mail tells one user: the builds of theirs that ended since the last one.</summary>
/// <param name="UserName">The user the e-mail is for.</param>
/// <param name="Builds">The builds, at least one.</param>
public sealed record BuildNotificationModel(string UserName, IReadOnlyList<BuildNotificationLine> Builds)
{
    /// <summary>The builds, failures first, then in the order they ended.</summary>
    // Computed on read, not stored: a copy made with "with { Builds = ... }" must not keep the old order
    public IReadOnlyList<BuildNotificationLine> Ordered =>
        [.. Builds.OrderBy(b => b.Outcome == BuildOutcome.Failed ? 0 : 1).ThenBy(b => b.FinishedAtUtc)];

    /// <summary>How many failed.</summary>
    public int FailedCount => Builds.Count(b => b.Outcome == BuildOutcome.Failed);

    /// <summary>How many completed.</summary>
    public int CompletedCount => Builds.Count(b => b.Outcome == BuildOutcome.Completed);

    /// <summary>The datasets of the builds (<c>org/ds</c>), each once, in the order of <see cref="Ordered"/>.</summary>
    public IReadOnlyList<string> Datasets => [.. Ordered.Select(b => b.Dataset).Distinct(StringComparer.Ordinal)];

    /// <summary>The counts in words, e.g. "2 builds failed, 3 completed".</summary>
    public string Summary
    {
        get
        {
            var failed = FailedCount;
            var completed = CompletedCount;

            static string Builds(int count) => count == 1 ? "1 build" : $"{count} builds";

            return (failed, completed) switch
            {
                (> 0, > 0) => $"{Builds(failed)} failed, {completed} completed",
                (> 0, _) => $"{Builds(failed)} failed",
                _ => $"{Builds(completed)} completed"
            };
        }
    }
}
