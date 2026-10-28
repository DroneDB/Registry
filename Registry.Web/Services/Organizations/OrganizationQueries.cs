#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Organizations;

/// <summary>Reading organizations (out of <c>OrganizationsManager</c>): the lists and one organization, with the owner shown by name and the caller's permissions.</summary>
public sealed class OrganizationQueries
{
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly RegistryContext _context;
    private readonly ApplicationDbContext _appContext;

    public OrganizationQueries(IAuthManager authManager, IDatasetAccessResolver access, RegistryContext context,
        ApplicationDbContext appContext)
    {
        _authManager = authManager;
        _access = access;
        _context = context;
        _appContext = appContext;
    }

    /// <summary>The organizations the current user can see (or owns, with <paramref name="ownedOnly"/>), with its permissions in each.</summary>
    public async Task<IEnumerable<OrganizationDto>> List(bool ownedOnly = false, CancellationToken ct = default)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (!await _authManager.CanListOrganizations(currentUser))
            throw new UnauthorizedException("Invalid user");

        var isAdmin = await _authManager.IsUserAdmin();

        // Use projection to load only necessary data
        // We compute the raw permission level in the query, then derive boolean permissions in memory
        var organizationsQuery = _context.Organizations
            .AsNoTracking()
            .Where(org => ownedOnly
                ? org.OwnerId == currentUser.Id
                : isAdmin ||
                  org.OwnerId == currentUser.Id ||
                  org.Slug == MagicStrings.PublicOrganizationSlug ||
                  org.Users.Any(u => u.UserId == currentUser.Id))
            .Select(org => new
            {
                org.CreationDate,
                org.Description,
                org.Slug,
                org.Name,
                org.IsPublic,
                Owner = org.OwnerId,
                IsOwnerOrAdmin = isAdmin || org.OwnerId == currentUser.Id,
                MemberPermission = org.Users.Where(u => u.UserId == currentUser.Id)
                    .Select(u => (OrganizationPermissions?)u.Permissions)
                    .FirstOrDefault()
            });

        var rawOrganizations = await organizationsQuery.ToListAsync(ct);

        var organizations = rawOrganizations.Select(org =>
        {
            var effectivePermission = org.IsOwnerOrAdmin
                ? OrganizationPermissions.Admin
                : org.MemberPermission;

            return new OrganizationDto
            {
                CreationDate = org.CreationDate,
                Description = org.Description,
                Slug = org.Slug,
                Name = org.Name,
                IsPublic = org.IsPublic,
                Owner = org.Owner,
                Permissions = effectivePermission != null
                    ? OrganizationPermissionsDto.FromPermissions(effectivePermission.Value)
                    : org.IsPublic
                        ? OrganizationPermissionsDto.ReadOnly
                        : null
            };
        }).ToList();

        await ResolveOwnerNamesAsync(organizations);

        return organizations;
    }

    /// <summary>The public organizations (open to anonymous callers).</summary>
    public async Task<IEnumerable<OrganizationDto>> ListPublic(CancellationToken ct = default)
    {
        // List all public organizations for data discovery
        // This endpoint is available to all users (including anonymous)
        var organizationsQuery = _context.Organizations
            .AsNoTracking()
            .Where(org => org.IsPublic)
            .Select(org => new OrganizationDto
            {
                CreationDate = org.CreationDate,
                Description = org.Description,
                Slug = org.Slug,
                Name = org.Name,
                IsPublic = org.IsPublic,
                Owner = org.OwnerId
            });

        var organizations = await organizationsQuery.ToListAsync(ct);

        await ResolveOwnerNamesAsync(organizations);

        return organizations;
    }

    /// <summary>
    /// <paramref name="org"/> as every organization answer shows it: the owner is the user name.
    /// </summary>
    public async Task<OrganizationDto> ToDtoWithOwnerNameAsync(Organization org)
    {
        var dto = org.ToDto();
        await ResolveOwnerNamesAsync([dto]);
        return dto;
    }

    /// <summary>
    /// Replaces the owner user id of each organization with the user name (null when the user no longer
    /// exists): the single place the organization answers (list, get, create) map the owner.
    /// </summary>
    private async Task ResolveOwnerNamesAsync(IReadOnlyCollection<OrganizationDto> organizations)
    {
        var ownerIds = organizations.Where(o => o.Owner != null)
                                   .Select(o => o.Owner)
                                   .Distinct()
                                   .ToArray();

        var usersMapper = await ResolveUserNamesAsync(ownerIds);

        foreach (var org in organizations.Where(org => org.Owner != null))
        {
            org.Owner = usersMapper.SafeGetValue(org.Owner);
        }
    }

    /// <summary>
    /// Resolves a set of user IDs to a {Id -> UserName} dictionary.
    /// Avoids the EF Core "WHERE Id IN (@p1..@pN)" parameter explosion by:
    ///   - using a single full-table projection when the requested set is large enough
    ///     that the IN clause would dominate (and the user table is bounded);
    ///   - falling back to a single small IN query for tiny inputs.
    /// Returns an empty dictionary when there are no IDs to resolve.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveUserNamesAsync(string[] userIds)
    {
        if (userIds == null || userIds.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        // Threshold tuned so that, beyond it, the parameterized IN-clause cost (network +
        // query plan cache pollution + provider per-parameter overhead) exceeds the cost of
        // a single full projection of the Users table. Hub-scale: users << datasets/orgs.
        const int InClauseThreshold = 64;

        if (userIds.Length <= InClauseThreshold)
        {
            return await _appContext.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.UserName, StringComparer.Ordinal);
        }

        // Single parameter-less query, then filter in memory.
        // The Users table is small relative to datasets/organizations.
        var wanted = new HashSet<string>(userIds, StringComparer.Ordinal);

        var allUsers = await _appContext.Users
            .AsNoTracking()
            .Select(u => new { u.Id, u.UserName })
            .ToListAsync();

        var result = new Dictionary<string, string>(wanted.Count, StringComparer.Ordinal);
        foreach (var u in allUsers)
        {
            if (wanted.Contains(u.Id))
                result[u.Id] = u.UserName;
        }

        return result;
    }

    /// <summary>One organization (read access) with the permissions of the current user in it.</summary>
    public async Task<OrganizationDto> GetOrganization(string orgSlug, CancellationToken ct = default)
    {
        var org = await _access.ResolveOrganizationAsync(orgSlug, AccessType.Read, ct: ct);

        var dto = await ToDtoWithOwnerNameAsync(org);

        // Populate current user's permissions in this organization
        var currentUser = await _authManager.GetCurrentUser();
        if (currentUser != null)
        {
            OrganizationPermissions? effectivePermission = null;

            if (await _authManager.IsUserAdmin())
            {
                effectivePermission = OrganizationPermissions.Admin;
            }
            else if (org.OwnerId == currentUser.Id)
            {
                effectivePermission = OrganizationPermissions.Admin;
            }
            else
            {
                // Load organization users if not already loaded
                if (!_context.Entry(org).Collection(o => o.Users).IsLoaded)
                    await _context.Entry(org).Collection(o => o.Users).LoadAsync(ct);

                var orgUser = org.Users?.FirstOrDefault(u => u.UserId == currentUser.Id);
                effectivePermission = orgUser?.Permissions;
            }

            if (effectivePermission != null)
            {
                dto.Permissions = OrganizationPermissionsDto.FromPermissions(effectivePermission.Value);
            }
            else if (org.IsPublic)
            {
                dto.Permissions = OrganizationPermissionsDto.ReadOnly;
            }
        }

        return dto;
    }
}
