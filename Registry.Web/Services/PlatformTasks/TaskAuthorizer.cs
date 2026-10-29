using System.Threading;
using System.Threading.Tasks;
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

    public TaskAuthorizer(IDatasetAccessResolver access, IJobIndexQuery query, IAuthManager authManager)
    {
        _access = access;
        _query = query;
        _authManager = authManager;
    }

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
            throw new ForbiddenException("Forbidden", noRetry: false);

        return job;
    }
}
