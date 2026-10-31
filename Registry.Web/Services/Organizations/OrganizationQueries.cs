using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Ports;
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
    private readonly IDdbManager _ddbManager;

    public OrganizationQueries(IAuthManager authManager, IDatasetAccessResolver access, RegistryContext context,
        ApplicationDbContext appContext, IDdbManager ddbManager)
    {
        _authManager = authManager;
        _access = access;
        _context = context;
        _appContext = appContext;
        _ddbManager = ddbManager;
    }

    /// <summary>What the datasets of an organization say about it: how many there are and when the newest write to an index happened (null when no index could be read).</summary>
    private sealed record OrganizationActivity(int DatasetCount, DateTime? LastWriteUtc);

    /// <summary>
    /// The organizations the current user can see (or owns, with <paramref name="ownedOnly"/>), with its permissions in each,
    /// by name. The dataset count and the last modified date are shown for the organizations the user owns, administers or
    /// belongs to; they are null for the others (the datasets of an organization are not public knowledge).
    /// </summary>
    public async Task<IEnumerable<OrganizationDto>> List(bool ownedOnly = false, CancellationToken ct = default)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null || !await _authManager.CanListOrganizations(currentUser))
            throw new UnauthorizedException("Invalid user");

        var isAdmin = await _authManager.IsUserAdmin();

        // Use projection to load only necessary data
        // We compute the raw permission level in the query, then derive boolean permissions in memory
        Expression<Func<Organization, bool>> isVisible = org => ownedOnly
            ? org.OwnerId == currentUser.Id
            : isAdmin ||
              org.OwnerId == currentUser.Id ||
              org.Slug == MagicStrings.PublicOrganizationSlug ||
              org.Users.Any(u => u.UserId == currentUser.Id);
        var visibleOrganizations = _context.Organizations.AsNoTracking().Where(isVisible);

        var organizationsQuery = visibleOrganizations
            .OrderBy(org => org.Name)
            .ThenBy(org => org.Slug)
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

        // Only for the organizations the user owns, administers or belongs to
        var memberSlugs = rawOrganizations
            .Where(org => org.IsOwnerOrAdmin || org.MemberPermission != null)
            .Select(org => org.Slug)
            .ToHashSet(StringComparer.Ordinal);
        var activity = await LoadActivityAsync(
            visibleOrganizations.Where(org => isAdmin || org.OwnerId == currentUser.Id ||
                                              org.Users.Any(u => u.UserId == currentUser.Id)), ct);

        var organizations = rawOrganizations.Select(org =>
        {
            var effectivePermission = org.IsOwnerOrAdmin
                ? OrganizationPermissions.Admin
                : org.MemberPermission;

            var dto = new OrganizationDto
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

            if (memberSlugs.Contains(org.Slug))
                SetActivity(dto, activity);

            return dto;
        }).ToList();

        await ResolveOwnerNamesAsync(organizations);

        return organizations;
    }

    /// <summary>The public organizations (open to anonymous callers), by name, without their dataset count and last modified date.</summary>
    public async Task<IEnumerable<OrganizationDto>> ListPublic(CancellationToken ct = default)
    {
        // List all public organizations for data discovery
        // This endpoint is available to all users (including anonymous)
        var organizationsQuery = _context.Organizations
            .AsNoTracking()
            .Where(org => org.IsPublic)
            .OrderBy(org => org.Name)
            .ThenBy(org => org.Slug)
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
    /// <paramref name="org"/> as every organization answer shows it: the owner is the user name. It has
    /// no dataset yet (a new organization): the last modified date is its creation date.
    /// </summary>
    public async Task<OrganizationDto> ToDtoWithOwnerNameAsync(Organization org)
    {
        var dto = org.ToDto();
        SetActivity(dto, EmptyActivity);
        await ResolveOwnerNamesAsync([dto]);
        return dto;
    }

    private static readonly IReadOnlyDictionary<string, OrganizationActivity> EmptyActivity =
        new Dictionary<string, OrganizationActivity>();

    /// <summary>
    /// The activity of each organization of <paramref name="organizations"/> that has datasets: one untracked
    /// projection of its datasets, then the state of the index files of each (file stats only). A dataset
    /// whose folder or index cannot be read is skipped: it counts, it does not date.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, OrganizationActivity>> LoadActivityAsync(
        IQueryable<Organization> organizations, CancellationToken ct)
    {
        var datasets = await _context.Datasets
            .AsNoTracking()
            .Where(ds => organizations.Any(org => org.Slug == ds.Organization.Slug))
            .Select(ds => new { OrgSlug = ds.Organization.Slug, ds.InternalRef })
            .ToListAsync(ct);

        return datasets
            .GroupBy(ds => ds.OrgSlug, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => new OrganizationActivity(g.Count(), g
                    .Select(ds => LastWriteOrNull(g.Key, ds.InternalRef))
                    .Max()),
                StringComparer.Ordinal);
    }

    private DateTime? LastWriteOrNull(string orgSlug, Guid internalRef)
    {
        try
        {
            // Open, never Get: a dataset without a folder must not be created by reading the list
            return _ddbManager.Open(orgSlug, internalRef).GetIndexFileState()?.LastWriteUtc;
        }
        catch (IOException)
        {
            // Also a folder that is gone (DirectoryNotFoundException)
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The dataset count and the last modified date of <paramref name="dto"/>: the newest write to the index of its
    /// datasets, or its creation date when no dataset has a readable index.
    /// </summary>
    private static void SetActivity(OrganizationDto dto, IReadOnlyDictionary<string, OrganizationActivity> activity)
    {
        activity.TryGetValue(dto.Slug!, out var found);
        dto.DatasetCount = found?.DatasetCount ?? 0;
        dto.LastModified = found?.LastWriteUtc ?? dto.CreationDate;
    }

    /// <summary>
    /// Replaces the owner user id of each organization with the user name (null when the user no longer
    /// exists): the single place the organization answers (list, get, create) map the owner.
    /// </summary>
    private async Task ResolveOwnerNamesAsync(IReadOnlyCollection<OrganizationDto> organizations)
    {
        var ownerIds = organizations.Select(o => o.Owner)
                                   .OfType<string>()
                                   .Distinct()
                                   .ToArray();

        var usersMapper = await ResolveUserNamesAsync(ownerIds);

        foreach (var org in organizations)
        {
            if (org.Owner is { } owner)
                org.Owner = usersMapper.SafeGetValue(owner);
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
    private async Task<Dictionary<string, string?>> ResolveUserNamesAsync(string[] userIds)
    {
        if (userIds == null || userIds.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

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

        var result = new Dictionary<string, string?>(wanted.Count, StringComparer.Ordinal);
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

        // Shown to its owner, administrators and members only
        dto.DatasetCount = null;
        dto.LastModified = null;

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
                SetActivity(dto, await LoadActivityAsync(_context.Organizations.AsNoTracking().Where(o => o.Slug == org.Slug), ct));
            }
            else if (org.IsPublic)
            {
                dto.Permissions = OrganizationPermissionsDto.ReadOnly;
            }
        }

        return dto;
    }
}
