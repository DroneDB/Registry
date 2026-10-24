#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The one implementation of organization membership rows (RQ-06), used by the organization member
/// routes (<c>/orgs/{orgSlug}/members</c>) and the administrator routes (<c>/users/{userName}/orgs</c>).
/// It does not authorize: each caller checks who may change the members first. Every grant records
/// <see cref="OrganizationUser.GrantedAt"/> and <see cref="OrganizationUser.GrantedBy"/>.
/// <para>Codes: an unknown organization or user 404, already a member 409, the organization's owner as
/// the target 400, not a member 404.</para>
/// </summary>
public sealed class OrganizationMembershipService
{
    private readonly RegistryContext _context;
    private readonly ApplicationDbContext _identity;
    private readonly ILookupNormalizer _normalizer;
    private readonly ILogger<OrganizationMembershipService> _logger;

    public OrganizationMembershipService(RegistryContext context, ApplicationDbContext identity,
        ILookupNormalizer normalizer, ILogger<OrganizationMembershipService> logger)
    {
        _context = context;
        _identity = identity;
        _normalizer = normalizer;
        _logger = logger;
    }

    /// <summary>The organization <paramref name="orgSlug"/> with its members, tracked.</summary>
    /// <exception cref="Exceptions.NotFoundException">There is no such organization.</exception>
    public async Task<Organization> GetWithMembersAsync(string orgSlug, CancellationToken ct = default) =>
        await _context.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.Slug == orgSlug, ct)
        ?? throw new NotFoundException($"Organization '{orgSlug}' not found");

    /// <summary>Makes <paramref name="userName"/> a member of <paramref name="organization"/> (loaded by
    /// <see cref="GetWithMembersAsync"/>) with <paramref name="permissions"/>, granted by <paramref name="grantedBy"/>.</summary>
    public async Task AddAsync(Organization organization, string userName, OrganizationPermissions permissions,
        User grantedBy, CancellationToken ct = default)
    {
        var user = await FindUserAsync(userName, ct);

        if (FindMembership(organization, user) != null)
            throw new ConflictException("User is already a member of this organization");

        if (organization.OwnerId == user.Id)
            throw new BadRequestException("Cannot add the owner of the organization as a member");

        _context.OrganizationsUsers.Add(new OrganizationUser
        {
            OrganizationSlug = organization.Slug,
            UserId = user.Id,
            Permissions = permissions,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = grantedBy.Id
        });
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserName} added to organization {OrgSlug} with permission {Permission} by {GrantedBy}",
            userName, organization.Slug, permissions, grantedBy.UserName);
    }

    /// <summary>Changes the permissions of the member <paramref name="userName"/>; the change is a new grant
    /// by <paramref name="grantedBy"/>.</summary>
    public async Task UpdateAsync(Organization organization, string userName, OrganizationPermissions permissions,
        User grantedBy, CancellationToken ct = default)
    {
        var (_, membership) = await FindMemberAsync(organization, userName,
            "Cannot change the permissions of the owner of the organization", ct);

        var oldPermission = membership.Permissions;
        membership.Permissions = permissions;
        membership.GrantedAt = DateTime.UtcNow;
        membership.GrantedBy = grantedBy.Id;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserName} permission in organization {OrgSlug} changed from {OldPermission} to {NewPermission} by {ChangedBy}",
            userName, organization.Slug, oldPermission, permissions, grantedBy.UserName);
    }

    /// <summary>Removes the member <paramref name="userName"/>.</summary>
    public async Task RemoveAsync(Organization organization, string userName, User removedBy,
        CancellationToken ct = default)
    {
        var (_, membership) = await FindMemberAsync(organization, userName,
            "Cannot remove the owner from the organization", ct);

        _context.OrganizationsUsers.Remove(membership);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserName} removed from organization {OrgSlug} by {RemovedBy}",
            userName, organization.Slug, removedBy.UserName);
    }

    /// <summary>
    /// Adds the members of <paramref name="sourceSlug"/> to <paramref name="targetSlug"/> (an organization
    /// merge): each keeps its permissions and its recorded grant (the merge, by <paramref name="grantedBy"/>,
    /// is the grant when none was recorded). A user who is already a member of the target, or its owner,
    /// is skipped. Returns the number of members added.
    /// </summary>
    public async Task<int> CopyMembersAsync(string sourceSlug, string targetSlug, User grantedBy,
        CancellationToken ct = default)
    {
        var source = await GetWithMembersAsync(sourceSlug, ct);
        var target = await GetWithMembersAsync(targetSlug, ct);
        var now = DateTime.UtcNow;

        var added = 0;
        foreach (var member in source.Users ?? [])
        {
            if (member.UserId == target.OwnerId || target.Users?.Any(u => u.UserId == member.UserId) == true)
                continue;

            _context.OrganizationsUsers.Add(new OrganizationUser
            {
                OrganizationSlug = target.Slug,
                UserId = member.UserId,
                Permissions = member.Permissions,
                GrantedAt = member.GrantedAt ?? now,
                GrantedBy = member.GrantedAt == null ? grantedBy.Id : member.GrantedBy
            });
            added++;
            _logger.LogInformation("Member {UserId} of {SourceOrgSlug} joins {TargetOrgSlug} as {Permission}",
                member.UserId, sourceSlug, targetSlug, member.Permissions);
        }

        await _context.SaveChangesAsync(ct);
        return added;
    }

    private async Task<User> FindUserAsync(string userName, CancellationToken ct)
    {
        var normalized = _normalizer.NormalizeName(userName);
        return await _identity.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, ct)
               ?? throw new NotFoundException($"User '{userName}' not found");
    }

    private async Task<(User User, OrganizationUser Membership)> FindMemberAsync(Organization organization,
        string userName, string ownerMessage, CancellationToken ct)
    {
        var user = await FindUserAsync(userName, ct);

        if (organization.OwnerId == user.Id)
            throw new BadRequestException(ownerMessage);

        var membership = FindMembership(organization, user)
                         ?? throw new NotFoundException("User is not a member of this organization");
        return (user, membership);
    }

    private static OrganizationUser? FindMembership(Organization organization, User user) =>
        organization.Users?.FirstOrDefault(u => u.UserId == user.Id);
}
