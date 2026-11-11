using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Managers;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>Loads a task of a dataset for a caller who may touch it (out of <c>TasksManager</c>).</summary>
public sealed class TaskAuthorizer
{
    private readonly IDatasetAccessResolver _access;
    private readonly IJobIndexQuery _query;
    private readonly IAuthManager _authManager;
    private readonly IHeavyToolRegistry? _tools;

    public TaskAuthorizer(IDatasetAccessResolver access, IJobIndexQuery query, IAuthManager authManager,
        IHeavyToolRegistry? tools = null)
    {
        _access = access;
        _query = query;
        _authManager = authManager;
        _tools = tools;
    }

    /// <summary>The ids of the tools that need only read access to the dataset (<see cref="IHeavyTool.RequiredAccess"/>).</summary>
    public IReadOnlySet<string> ReadTools =>
        _tools is null
            ? new HashSet<string>()
            : _tools.All.Where(t => t.RequiredAccess == HeavyToolPermission.Read).Select(t => t.Id).ToHashSet();

    /// <summary>
    /// Resolves a task after checking dataset access and per-task ownership: the task's owner, the dataset's
    /// owner or an admin, or anybody for a system task.
    /// </summary>
    /// <exception cref="NotFoundException">The dataset or the task does not exist.</exception>
    /// <exception cref="ForbiddenException">The caller owns neither the task nor the dataset.</exception>
    public async Task<JobIndex> LoadAsync(string orgSlug, string dsSlug, string id, AccessType access, CancellationToken ct)
    {
        // A missing/unknown dataset surfaces as an unhandled exception; the global
        // classifier maps it to the same 404 the per-action build used to produce.
        var ds = (await _access.ResolveAsync(orgSlug, dsSlug, access, ct: ct)).Dataset;

        var job = await _query.GetByJobIdAsync(orgSlug, dsSlug, id, ct)
                  ?? throw new NotFoundException("Task not found");

        // Ownership: owner, dataset owner/admin, or system task.
        var user = await _authManager.GetCurrentUser();
        var isOwner = job.UserId is null
                      || (user is not null && job.UserId == user.Id)
                      || await _authManager.IsOwnerOrAdmin(ds);
        if (!isOwner)
            throw new ForbiddenException(NotYoursMessage, noRetry: false);

        return job;
    }

    /// <summary>
    /// Resolves a task to cancel. A caller with write access to the dataset cancels as <see cref="LoadAsync"/> allows
    /// with <see cref="AccessType.Write"/>. A caller without it cancels only a task it started of a tool that needs only
    /// read access (a contour run, a raster export: NFR-001, FR-XC-15); anything else is refused as before, by the write
    /// check.
    /// </summary>
    /// <exception cref="NotFoundException">The dataset or the task does not exist.</exception>
    /// <exception cref="ForbiddenException">The caller may not cancel the task.</exception>
    public async Task<JobIndex> LoadToCancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct)
    {
        var ds = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Dataset;
        if (!await _authManager.RequestAccess(ds, AccessType.Write))
        {
            var job = await _query.GetByJobIdAsync(orgSlug, dsSlug, id, ct);
            var user = await _authManager.GetCurrentUser();
            if (job is not null && new TaskCaller(user?.Id, false, false, ReadTools).OwnsReadTask(job.UserId, job.ToolId))
                return job;
        }

        return await LoadAsync(orgSlug, dsSlug, id, AccessType.Write, ct);
    }

    /// <summary>Why a caller cannot touch a task of somebody else (D-033).</summary>
    public const string NotYoursMessage =
        "Only the user who started this task, the owner of the dataset or an administrator can do this.";

    /// <summary>
    /// Who reads the tasks of <paramref name="ds"/>, which the caller already resolved: the user, whether they own the
    /// dataset (or are an admin) and whether they may write to it. The actions of each task follow from it
    /// (<see cref="TaskCaller.ActionsOn"/>), by the rule of <see cref="LoadAsync"/>.
    /// </summary>
    public async Task<TaskCaller> CallerAsync(Dataset ds)
    {
        var user = await _authManager.GetCurrentUser();
        var ownsDataset = await _authManager.IsOwnerOrAdmin(ds);
        var canWrite = await _authManager.RequestAccess(ds, AccessType.Write);
        return new TaskCaller(user?.Id, ownsDataset, canWrite, ReadTools);
    }

    /// <summary>The id of the current user, or null for an anonymous caller.</summary>
    public async Task<string?> CurrentUserIdAsync() => (await _authManager.GetCurrentUser())?.Id;
}
