using System;
using Registry.Web.Data.Models;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>Who asked for a build, as its job records it.</summary>
/// <param name="UserId">The user id the job carries: a user, the automatic build service, or null (a task output).</param>
/// <param name="JobId">The build job; null when the report does not come from one job (the retries gave up).</param>
public sealed record BuildRequest(string? UserId, string? JobId)
{
    /// <summary>The request of an entry whose automatic retries ran out: no job ended, the build service gave up.</summary>
    public static BuildRequest RetriesGivenUp { get; } = new(MagicStrings.AutoBuildServiceUserId, null);
}

/// <summary>The final outcome of the build of one entry, and the request it answers.</summary>
/// <param name="OrgSlug">The organization of the dataset.</param>
/// <param name="DsSlug">The dataset.</param>
/// <param name="Path">The entry, relative to the dataset root.</param>
/// <param name="Hash">The hash of the entry that was built.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">
/// A short reason when it failed, the missing files when it waits for files (see <see cref="ShortError"/>); null otherwise.
/// </param>
/// <param name="Request">Who asked.</param>
public sealed record BuildReport(
    string OrgSlug,
    string DsSlug,
    string Path,
    string Hash,
    BuildOutcome Outcome,
    string? Error,
    BuildRequest Request)
{
    /// <summary>
    /// <paramref name="error"/> as an e-mail shows it: the dataset folder replaced (the e-mail goes outside the server)
    /// and at most <see cref="BuildNotification.ErrorMaxLength"/> characters. Null for a blank text.
    /// </summary>
    public static string? ShortError(string? error, string? datasetFolderPath)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;

        var scrubbed = JobLogText.ScrubDatasetPath(error.Trim(), datasetFolderPath);
        return scrubbed.Length <= BuildNotification.ErrorMaxLength
            ? scrubbed
            : string.Concat(scrubbed.AsSpan(0, BuildNotification.ErrorMaxLength - 1), "…");
    }
}

/// <summary>The user a build notification goes to, and the request it answers.</summary>
/// <param name="UserId">The user.</param>
/// <param name="RequestJobId">The job of the user's request (see <see cref="BuildNotification.RequestJobId"/>).</param>
public sealed record BuildRequester(string UserId, string? RequestJobId);
