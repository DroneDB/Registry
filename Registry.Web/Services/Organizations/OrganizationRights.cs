using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Organizations;

/// <summary>
/// What a user is in an organization, by its members and not by the datasets: the one definition of a member and of an
/// administrator of an organization, used wherever a feature is for "the members" or "the administrators" (the member
/// list, the management of the members, the report settings).
/// </summary>
public sealed class OrganizationRights
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;

    public OrganizationRights(IAuthManager authManager, RegistryContext context, IDatasetResolver datasets)
    {
        _authManager = authManager;
        _context = context;
        _datasets = datasets;
    }

    /// <summary>
    /// Throws unless the current user is a member of the organization <paramref name="orgSlug"/>
    /// (<see cref="IsMemberAsync"/>): <c>NotFoundException</c> for an unknown organization, <c>ForbiddenException</c>
    /// for a user who is not a member.
    /// </summary>
    public async Task RequireMemberAsync(string orgSlug, CancellationToken ct = default)
    {
        var org = await _datasets.GetRequiredOrganizationAsync(orgSlug, ct: ct);
        if (!await IsMemberAsync(org, await _authManager.GetCurrentUserId(), ct))
            throw new ForbiddenException(AccessDeniedMessages.OrganizationMemberRequired);
    }

    /// <summary>
    /// Throws unless the current user is an administrator of the organization <paramref name="orgSlug"/>
    /// (<see cref="CanManageAsync"/>): <c>NotFoundException</c> for an unknown organization, <c>ForbiddenException</c>
    /// for a user who may not manage it.
    /// </summary>
    public async Task RequireAdministratorAsync(string orgSlug, CancellationToken ct = default)
    {
        var org = await _datasets.GetRequiredOrganizationAsync(orgSlug, ct: ct);
        if (!await CanManageAsync(org, await _authManager.GetCurrentUserId(), ct))
            throw new ForbiddenException(AccessDeniedMessages.OrganizationAdminRequired);
    }

    /// <summary>
    /// Whether the user <paramref name="userId"/> (null: anonymous) is an administrator of <paramref name="org"/>: an
    /// administrator of the instance, its owner, or a member whose permission is <c>Admin</c>.
    /// </summary>
    public async Task<bool> CanManageAsync(Organization org, string? userId, CancellationToken ct = default)
    {
        if (userId == null)
            return false;

        // System admin can always manage
        if (await _authManager.IsUserAdmin()) return true;

        // Owner can always manage
        if (org.OwnerId == userId) return true;

        // Check if member has Admin permission
        var membership = await MembershipAsync(org, userId, ct);
        return membership != null && membership.Permissions.CanManageMembers();
    }

    /// <summary>
    /// Whether the user <paramref name="userId"/> (null: anonymous) is a member of <paramref name="org"/>, whatever the
    /// permission: an administrator of the instance, its owner, or one of its members.
    /// </summary>
    public async Task<bool> IsMemberAsync(Organization org, string? userId, CancellationToken ct = default)
    {
        if (userId == null)
            return false;

        if (await _authManager.IsUserAdmin() || org.OwnerId == userId)
            return true;

        return await MembershipAsync(org, userId, ct) != null;
    }

    // From the members the organization was loaded with, else from the table
    private async Task<OrganizationUser?> MembershipAsync(Organization org, string userId, CancellationToken ct) =>
        org.Users != null
            ? org.Users.FirstOrDefault(u => u.UserId == userId)
            : await _context.OrganizationsUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.OrganizationSlug == org.Slug && u.UserId == userId, ct);
}
