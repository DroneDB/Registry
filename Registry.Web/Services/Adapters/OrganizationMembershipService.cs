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
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IOrganizationMembershipService"/>: membership rows in the registry database, users
/// looked up by their normalized name in the identity store (the lookup ASP.NET Identity uses).
/// </summary>
public sealed class OrganizationMembershipService : IOrganizationMembershipService
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

    public async Task<Organization> GetWithMembersAsync(string orgSlug, CancellationToken ct = default) =>
        await _context.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.Slug == orgSlug, ct)
        ?? throw new NotFoundException($"Organization '{orgSlug}' not found");

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
