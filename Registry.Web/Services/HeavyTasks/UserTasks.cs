#nullable enable
using System;
using System.Linq;
using System.Linq.Expressions;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// Which jobs are tasks of a user: the jobs the concurrency limits of the processing platform count.
/// Dataset builds have their own queues, and a job of the automatic build service or of no user (a task
/// the system submits) runs on no user's behalf, so neither takes a user's task slot; counting them let
/// pending builds and their retries fill the slots and refuse every download and import.
/// </summary>
public static class UserTasks
{
    /// <summary>Whether <paramref name="userId"/> names a user, not the automatic build service or nobody.</summary>
    public static bool IsUser(string? userId) =>
        !string.IsNullOrEmpty(userId) && userId != MagicStrings.AutoBuildServiceUserId;

    /// <summary>
    /// The jobs that count toward the task limits: active, of a tool other than the build, and of a user
    /// (<see cref="IsUser"/>, written out so that it translates to SQL).
    /// </summary>
    public static readonly Expression<Func<JobIndex, bool>> CountTowardLimits = x =>
        TaskStateCatalog.Active.Contains(x.CurrentState)
        && x.ToolId != HeavyToolIds.Build
        && !string.IsNullOrEmpty(x.UserId) && x.UserId != MagicStrings.AutoBuildServiceUserId;
}
