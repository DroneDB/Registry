#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// The one rule of the slug and folder changes of a dataset (the slug repair, the rename, the move to another
/// organization): not while it has jobs queued or running (<see cref="SlugKeyedRows.BlockingJobStates"/>). A job
/// names its dataset by the slugs it was queued with, and only the history rows follow a change
/// (<see cref="SlugKeyedRows"/>), so a job that outlived the change would look for a dataset that is not there:
/// it is refused with the reason below, and the caller retries when the jobs finish.
/// </summary>
public static class DatasetJobsRule
{
    /// <summary>Why a dataset is left as it is.</summary>
    public const string Reason = "active jobs";

    /// <summary><see cref="Reason"/> with what the caller can do.</summary>
    public const string RetryReason = Reason + ", retry when they finish";

    /// <summary>The 409 of a rename or a move that finds the jobs of the dataset.</summary>
    public static string Message(string orgSlug, string dsSlug) =>
        $"Dataset '{orgSlug}/{dsSlug}' cannot change its slug or organization now: {RetryReason}";

    /// <exception cref="ConflictException">The dataset has jobs queued or running.</exception>
    public static async Task EnsureNoActiveJobsAsync(this RegistryContext context, string orgSlug, string dsSlug,
        CancellationToken ct = default)
    {
        if (await context.HasBlockingJobsAsync(orgSlug, dsSlug, ct))
            throw new ConflictException(Message(orgSlug, dsSlug));
    }
}
