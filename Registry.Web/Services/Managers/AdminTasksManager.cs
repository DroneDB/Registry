#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Admin-only manager backing the global tasks dashboard (spec §B.1.2). Reuses the
/// existing JobIndex query layer and resolves task owner identities across the
/// Identity DbContext (no cross-context SQL join).
/// </summary>
public sealed class AdminTasksManager : IAdminTasksManager
{
    private readonly IJobIndexQuery _query;
    private readonly IAuthManager _authManager;
    private readonly UserManager<User> _userManager;
    private readonly int _artifactTtlHours;

    public AdminTasksManager(
        IJobIndexQuery query,
        IAuthManager authManager,
        UserManager<User> userManager,
        IOptions<AppSettings> appSettings)
    {
        _query = query;
        _authManager = authManager;
        _userManager = userManager;
        _artifactTtlHours = (appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings()).ArtifactTtlHours;
    }

    public async Task<AdminTaskListDto> ListAsync(string? toolId, string? state, string? userId,
        int skip, int take, CancellationToken ct = default)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can list all tasks");

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        var filter = new JobIndexGlobalQueryFilter(toolId, state, userId, skip, take);

        // Sequential: both queries share the same (non-thread-safe) RegistryContext.
        var rows = await _query.QueryGlobalAsync(filter, ct);
        var total = await _query.CountGlobalAsync(toolId, state, userId, ct);

        var ownerNames = await ResolveOwnerNamesAsync(rows, ct);

        var items = rows.Select(j => ToSummary(j, ownerNames)).ToArray();
        return new AdminTaskListDto(items, total, skip, take);
    }

    /// <summary>
    /// Batch-resolves userId → userName for the current page. JobIndex lives in
    /// RegistryContext while users live in the Identity DbContext, so we collect the
    /// distinct ids and look them up via the Identity store (no cross-context join).
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveOwnerNamesAsync(
        IReadOnlyCollection<JobIndex> rows, CancellationToken ct)
    {
        var ids = rows
            .Where(r => !string.IsNullOrEmpty(r.UserId))
            .Select(r => r.UserId!)
            .Distinct()
            .ToArray();

        var map = new Dictionary<string, string>();
        if (ids.Length == 0) return map;

        var users = await _userManager.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName })
            .ToArrayAsync(ct);

        foreach (var u in users)
            map[u.Id] = u.UserName ?? u.Id;

        return map;
    }

    private AdminTaskSummaryDto ToSummary(JobIndex j, IReadOnlyDictionary<string, string> ownerNames)
    {
        string? owner = null;
        if (!string.IsNullOrEmpty(j.UserId) && ownerNames.TryGetValue(j.UserId, out var name))
            owner = name;

        return new AdminTaskSummaryDto(
            j.JobId, j.OrgSlug, j.DsSlug, j.ToolId, j.ToolVersion, j.CurrentState,
            j.ProgressPercent, j.PhaseMessage, j.CreatedAtUtc, j.ProcessingAtUtc, FinishedAt(j),
            j.Path, j.UserId, owner, j.ErrorType, ArtifactExpiresAt(j));
    }

    private static DateTime? FinishedAt(JobIndex j) =>
        j.SucceededAtUtc ?? j.FailedAtUtc ?? j.DeletedAtUtc;

    /// <summary>
    /// Server-authoritative artifact expiry: the work directory is swept
    /// <c>ArtifactTtlHours</c> after completion (see <c>HeavyTaskJobWrapper</c>).
    /// Null when the task has no downloadable artifact.
    /// </summary>
    private DateTime? ArtifactExpiresAt(JobIndex j) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is not null && j.SucceededAtUtc is { } finished
            ? finished.AddHours(Math.Max(1, _artifactTtlHours))
            : null;
}
