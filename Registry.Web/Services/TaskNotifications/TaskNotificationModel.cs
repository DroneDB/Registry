using System;
using System.Globalization;

namespace Registry.Web.Services.TaskNotifications;

/// <summary>What a task notification e-mail tells one user: a task of theirs that ended (FR-PRC-40).</summary>
/// <param name="UserName">The user the e-mail is for.</param>
/// <param name="Task">What the task is (the title of its tool, e.g. "Photogrammetry (NodeODX)").</param>
/// <param name="Label">The task's target files (the label of FR-XC-25), or null when it has none.</param>
/// <param name="OrgSlug">The organization of the dataset.</param>
/// <param name="DsSlug">The dataset.</param>
/// <param name="Succeeded">Whether the task succeeded (else it failed).</param>
/// <param name="Error">
/// Why it failed: the message of the task error (FR-XC-06), the one that the task list shows, with no server path in it;
/// null for a task that succeeded.
/// </param>
/// <param name="Duration">How long it ran; null when its start is not known.</param>
/// <param name="DatasetRemoved">
/// Whether the task failed in a dataset that it created, which the rollback of the task deletes (U-114): the e-mail links
/// to the organization instead of the Tasks tab of a dataset that will be gone, and says so.
/// </param>
public sealed record TaskNotificationModel(
    string UserName,
    string Task,
    string? Label,
    string OrgSlug,
    string DsSlug,
    bool Succeeded,
    string? Error,
    TimeSpan? Duration,
    bool DatasetRemoved = false)
{
    /// <summary>The dataset as the Hub names it: <c>org/ds</c>.</summary>
    public string Dataset => $"{OrgSlug}/{DsSlug}";

    /// <summary>How the task ended in a word: <c>completed</c> or <c>failed</c>.</summary>
    public string Result => Succeeded ? "completed" : "failed";

    /// <summary>The duration in words ("17 min 4 s", "2 h 5 min", "45 s"); null when it is not known.</summary>
    public string? DurationText => Duration is { } duration ? Format(duration) : null;

    /// <summary>The words of <paramref name="duration"/>, rounded to the second; the two largest units that are not zero.</summary>
    public static string Format(TimeSpan duration)
    {
        var seconds = (long)Math.Round(Math.Max(0, duration.TotalSeconds), MidpointRounding.AwayFromZero);
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var rest = seconds % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h {minutes} min")
            : minutes > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min {rest} s")
                : string.Create(CultureInfo.InvariantCulture, $"{rest} s");
    }
}
