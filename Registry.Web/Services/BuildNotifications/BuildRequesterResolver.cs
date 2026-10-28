#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// <see cref="IBuildRequesterResolver"/> from the job index (<see cref="Data.Models.JobIndex"/>: the user, the parent
/// task, the path, the hash and the state of every job).
/// </summary>
public sealed class BuildRequesterResolver : IBuildRequesterResolver
{
    private readonly RegistryContext _db;

    public BuildRequesterResolver(RegistryContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public async Task<BuildRequester?> ResolveAsync(BuildReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var request = report.Request;

        if (UserTasks.IsUser(request.UserId))
            return new BuildRequester(request.UserId!, request.JobId);

        // A task output (an archive extract builds its files): the user of the task
        if (string.IsNullOrEmpty(request.UserId))
            return request.JobId == null ? null : await UserOfAsync(request.JobId, request.JobId, ct).ConfigureAwait(false);

        // An automatic retry (the pending builds run as the build service): the user whose build of this content failed
        return await RetriedRequestAsync(report, request.IsRetriesGivenUp, ct).ConfigureAwait(false);
    }

    // The user of the job, or of its parent task when it has none; the line answers requestJobId
    private async Task<BuildRequester?> UserOfAsync(string jobId, string? requestJobId, CancellationToken ct)
    {
        var job = await _db.JobIndices.AsNoTracking().Where(j => j.JobId == jobId)
            .Select(j => new { j.UserId, j.ParentJobId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (job == null)
            return null;
        if (UserTasks.IsUser(job.UserId))
            return new BuildRequester(job.UserId!, requestJobId);
        if (!string.IsNullOrEmpty(job.UserId) || job.ParentJobId == null)
            return null;

        var parentUser = await _db.JobIndices.AsNoTracking().Where(j => j.JobId == job.ParentJobId)
            .Select(j => j.UserId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return UserTasks.IsUser(parentUser) ? new BuildRequester(parentUser!, requestJobId) : null;
    }

    // The latest request of a user (or of a task) to build this content, when it failed and nothing built it since: the
    // retries that follow a failure are that request's. A rescan or a completeness sweep of a built entry finds a
    // succeeded build after the request (or a request that did not fail) and answers nobody.
    // When the retries gave up, a request that succeeded counts too: libddb still lists the entry pending with this hash,
    // so that success left it waiting for a missing file (a companion file, a texture) and the retries never built it.
    // The job states cannot say more: the retries of a missing file succeed too, without building, so no "built since"
    // check is possible there.
    private async Task<BuildRequester?> RetriedRequestAsync(BuildReport report, bool givenUp, CancellationToken ct)
    {
        var self = report.Request.JobId;
        var builds = _db.JobIndices.AsNoTracking().Where(j =>
            j.OrgSlug == report.OrgSlug && j.DsSlug == report.DsSlug && j.Path == report.Path && j.Hash == report.Hash &&
            j.JobId != self);

        var request = await builds
            .Where(j => j.UserId != MagicStrings.AutoBuildServiceUserId && (j.UserId != null || j.ParentJobId != null))
            .OrderByDescending(j => j.CreatedAtUtc)
            .Select(j => new { j.JobId, j.CurrentState, j.FailedAtUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (request == null)
            return null;

        if (givenUp && request.CurrentState == SucceededState.StateName)
            return await UserOfAsync(request.JobId, request.JobId, ct).ConfigureAwait(false);

        if (request.CurrentState != FailedState.StateName)
            return null;

        var builtSince = await builds
            .AnyAsync(j => j.CurrentState == SucceededState.StateName && j.SucceededAtUtc > request.FailedAtUtc, ct)
            .ConfigureAwait(false);
        return builtSince ? null : await UserOfAsync(request.JobId, request.JobId, ct).ConfigureAwait(false);
    }
}
