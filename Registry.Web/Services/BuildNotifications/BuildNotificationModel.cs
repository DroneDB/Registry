#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>How a build ended, as far as an e-mail to its requester is concerned.</summary>
public enum BuildOutcome
{
    /// <summary>The entry is built.</summary>
    Completed,

    /// <summary>The build failed for good: no automatic retry is left.</summary>
    Failed,

    /// <summary>
    /// The build waits for files the dataset lacks (an .obj without its .mtl): it runs when one of them is added. Told
    /// once per content of the entry, with the failures (a user who wants only the failed builds gets it too).
    /// </summary>
    WaitingForFiles
}

/// <summary>One finished build in a notification e-mail.</summary>
/// <param name="OrgSlug">The organization of the dataset.</param>
/// <param name="DsSlug">The dataset.</param>
/// <param name="Path">The entry that was built, relative to the dataset root.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">
/// A short reason when it failed, the missing files (dataset-relative) when it waits for files; no server paths in it;
/// null otherwise.
/// </param>
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
    // The order of the outcomes in an e-mail and in its summary: what needs the user first
    private static readonly (BuildOutcome Outcome, string Words)[] OutcomeOrder =
    [
        (BuildOutcome.Failed, "failed"),
        (BuildOutcome.WaitingForFiles, "waiting for files"),
        (BuildOutcome.Completed, "completed")
    ];

    /// <summary>The builds, failures first, then the builds waiting for files, each kind in the order they ended.</summary>
    // Computed on read, not stored: a copy made with "with { Builds = ... }" must not keep the old order
    public IReadOnlyList<BuildNotificationLine> Ordered =>
        [.. Builds.OrderBy(b => Array.FindIndex(OutcomeOrder, o => o.Outcome == b.Outcome)).ThenBy(b => b.FinishedAtUtc)];

    /// <summary>How many failed.</summary>
    public int FailedCount => CountOf(BuildOutcome.Failed);

    /// <summary>How many wait for files.</summary>
    public int WaitingCount => CountOf(BuildOutcome.WaitingForFiles);

    /// <summary>How many completed.</summary>
    public int CompletedCount => CountOf(BuildOutcome.Completed);

    private int CountOf(BuildOutcome outcome) => Builds.Count(b => b.Outcome == outcome);

    /// <summary>The datasets of the builds (<c>org/ds</c>), each once, in the order of <see cref="Ordered"/>.</summary>
    public IReadOnlyList<string> Datasets => [.. Ordered.Select(b => b.Dataset).Distinct(StringComparer.Ordinal)];

    /// <summary>The counts in words, e.g. "2 builds failed, 1 waiting for files, 3 completed".</summary>
    public string Summary
    {
        get
        {
            var counts = OutcomeOrder.Select(o => (Count: CountOf(o.Outcome), o.Words)).Where(c => c.Count > 0).ToList();
            if (counts.Count == 0)
                return "0 builds completed";

            // The noun goes with the first count only: "2 builds failed, 3 completed"
            var first = counts[0].Count == 1 ? "1 build" : $"{counts[0].Count} builds";
            return string.Join(", ", counts.Select((c, i) => $"{(i == 0 ? first : c.Count.ToString(CultureInfo.InvariantCulture))} {c.Words}"));
        }
    }
}
