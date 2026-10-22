using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// CRUD for organizations including creation, editing, deletion, and member management.
/// </summary>
public class OrganizationsManager : IOrganizationsManager
{
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly IOrganizationMembershipService _memberships;
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IDatasetsManager _datasetManager;
    private readonly ApplicationDbContext _appContext;
    private readonly IOptions<AccountOptions> _accounts;
    private readonly ILogger<OrganizationsManager> _logger;

    public OrganizationsManager(
        IAuthManager authManager,
        IDatasetAccessResolver access,
        RegistryContext context,
        IDatasetResolver datasets,
        IDatasetsManager datasetManager,
        ApplicationDbContext appContext,
        IOptions<AccountOptions> accounts,
        ILogger<OrganizationsManager> logger,
        IOrganizationMembershipService memberships)
    {
        _authManager = authManager;
        _access = access;
        _context = context;
        _datasets = datasets;
        _datasetManager = datasetManager;
        _appContext = appContext;
        _accounts = accounts;
        _logger = logger;
        _memberships = memberships;
    }

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
    private async Task<OrganizationDto> ToDtoWithOwnerNameAsync(Organization org)
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
    private async Task<Dictionary<string, string>> ResolveUserNamesAsync(IReadOnlyCollection<string> userIds)
    {
        if (userIds == null || userIds.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        // Threshold tuned so that, beyond it, the parameterized IN-clause cost (network +
        // query plan cache pollution + provider per-parameter overhead) exceeds the cost of
        // a single full projection of the Users table. Hub-scale: users << datasets/orgs.
        const int InClauseThreshold = 64;

        if (userIds.Count <= InClauseThreshold)
        {
            return await _appContext.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.UserName, StringComparer.Ordinal);
        }

        // Single parameter-less query, then filter in memory.
        // The Users table is small relative to datasets/organizations.
        var wanted = userIds is HashSet<string> hs
            ? hs
            : new HashSet<string>(userIds, StringComparer.Ordinal);

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

    public async Task<OrganizationDto> Get(string orgSlug, CancellationToken ct = default)
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

    public Task<OrganizationDto> AddUserDefaultAsync(string userId, string userName) =>
        AddNew(new OrganizationDto
        {
            Name = userName,
            IsPublic = false,
            CreationDate = DateTime.UtcNow,
            Owner = userId,
            Slug = _datasets.GetFreeOrganizationSlug(userName)
        }, skipAuthCheck: true);

    public async Task<OrganizationDto> AddNew(OrganizationDto organization, bool skipAuthCheck = false,
        CancellationToken ct = default)
    {
        if (!skipAuthCheck)
        {
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            if (!organization.Slug.IsValidSlug())
                throw new BadRequestException("Invalid organization orgSlug");

            var existingOrg = _context.Organizations.FirstOrDefault(item => item.Slug == organization.Slug);

            if (existingOrg != null)
                throw new ConflictException("The organization already exists");

            if (!await _authManager.IsUserAdmin())
            {
                // If the owner is specified it should be the current user
                if (organization.Owner != null && organization.Owner != currentUser.Id)
                    throw new ForbiddenException(
                        "Cannot create a new organization that belongs to a different user");

                // The current user is the owner
                organization.Owner = currentUser.Id;
            }
            else
            {
                // If no owner specified, the owner is the current user
                if (organization.Owner == null)
                    organization.Owner = currentUser.Id;
                else
                {
                    // Otherwise check if user exists
                    if (!await _authManager.UserExists(organization.Owner))
                        throw new BadRequestException($"Cannot find user with name '{organization.Owner}'");
                }
            }
        }
        else
        {
            if (!organization.Slug.IsValidSlug())
                throw new BadRequestException("Invalid organization orgSlug");
        }

        var org = organization.ToEntity();
        org.CreationDate = DateTime.UtcNow;

        // Honoured until the organization is written (RQ-13)
        ct.ThrowIfCancellationRequested();
        await _context.Organizations.AddAsync(org, CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);

        return await ToDtoWithOwnerNameAsync(org);
    }

    public async Task Edit(string orgSlug, OrganizationDto organization, CancellationToken ct = default)
    {
        var org = await _access.ResolveOrganizationAsync(orgSlug, AccessType.Write, withTracking: true, ct: ct);

        // The owner stays unless the request names another one, which only an administrator may do (the Hub
        // sends no owner: a member who edited the organization, or an administrator, used to become its owner)
        if (organization.Owner != null && organization.Owner != org.OwnerId)
        {
            if (!await _authManager.IsUserAdmin())
                throw new ForbiddenException("Only administrators can change the owner of an organization");

            if (!await _authManager.UserExists(organization.Owner))
                throw new BadRequestException($"Cannot find user with id '{organization.Owner}'");

            org.OwnerId = organization.Owner;
        }

        org.IsPublic = organization.IsPublic;
        org.Name = organization.Name;
        org.Description = organization.Description;

        await _context.SaveChangesAsync(CancellationToken.None);
    }

    public async Task Delete(string orgSlug, CancellationToken ct = default)
    {
        // Honoured while the organization is looked up; its datasets are then deleted to the end (RQ-13)
        var org = await _access.ResolveOrganizationAsync(orgSlug, AccessType.Delete, withTracking: true,
            withDatasets: true, ct: ct);

        foreach (var ds in org.Datasets.ToArray())
        {
            // TODO: To check and re-check and re-check and re-check
            await _datasetManager.Delete(org.Slug, ds.Slug, CancellationToken.None);
        }

        _context.Organizations.Remove(org);
        await _context.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<MergeOrganizationResultDto> Merge(
        string sourceOrgSlug,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict,
        bool deleteSourceOrganization = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceOrgSlug))
            throw new BadRequestException("Source organization slug is required");

        if (string.IsNullOrWhiteSpace(destOrgSlug))
            throw new BadRequestException("Destination organization slug is required");

        if (sourceOrgSlug == destOrgSlug)
            throw new BadRequestException("Source and destination organizations cannot be the same");

        // Validate public organization cannot be merged
        if (sourceOrgSlug == MagicStrings.PublicOrganizationSlug)
            throw new BadRequestException("Cannot merge the public organization");

        // Honoured while the merge is prepared; the moves then run to completion (RQ-13)
        var sourceOrg = await _datasets.GetOrganizationAsync(sourceOrgSlug, withTracking: true, withDatasets: true,
            ct: ct);
        var destOrg = await _datasets.GetOrganizationAsync(destOrgSlug, withTracking: true, ct: ct);

        _logger.LogInformation("Starting merge of organization '{SourceOrgSlug}' into '{DestOrgSlug}'",
            sourceOrgSlug, destOrgSlug);

        var result = new MergeOrganizationResultDto
        {
            SourceOrgSlug = sourceOrgSlug,
            DestinationOrgSlug = destOrgSlug,
            SourceOrganizationDeleted = false
        };

        // Get all datasets from source organization
        var datasetSlugs = sourceOrg.Datasets.Select(d => d.Slug).ToArray();

        if (datasetSlugs.Length > 0)
        {
            // Move all datasets to destination organization
            var moveResults = await _datasetManager.MoveToOrganization(
                sourceOrgSlug,
                datasetSlugs,
                destOrgSlug,
                conflictResolution, CancellationToken.None);

            result.DatasetResults = [.. moveResults];
            result.DatasetsMovedCount = result.DatasetResults.Count(r => r.Success);
            result.DatasetsFailedCount = result.DatasetResults.Count(r => !r.Success);
        }
        else
        {
            result.DatasetResults = [];
            result.DatasetsMovedCount = 0;
            result.DatasetsFailedCount = 0;
        }

        // The members of the source join the destination with their permissions (the source's members
        // were never loaded here, so a merge used to drop every one of them)
        await _memberships.CopyMembersAsync(sourceOrgSlug, destOrgSlug, await _authManager.GetCurrentUser(), CancellationToken.None);

        // Delete source organization if requested and all datasets were moved successfully
        if (deleteSourceOrganization)
        {
            if (result.DatasetsFailedCount == 0)
            {
                _logger.LogInformation("Deleting source organization '{SourceOrgSlug}' after successful merge", sourceOrgSlug);

                // Reload the organization to ensure we have the latest state
                sourceOrg = await _datasets.GetOrganizationAsync(sourceOrgSlug, withTracking: true, ct: CancellationToken.None);

                // Clear remaining users
                sourceOrg.Users?.Clear();

                _context.Organizations.Remove(sourceOrg);
                await _context.SaveChangesAsync(CancellationToken.None);

                result.SourceOrganizationDeleted = true;
            }
            else
            {
                _logger.LogWarning("Not deleting source organization '{SourceOrgSlug}' because some datasets failed to move",
                    sourceOrgSlug);
            }
        }

        _logger.LogInformation("Merge completed: {DatasetsMovedCount} datasets moved, {DatasetsFailedCount} failed, source deleted: {SourceDeleted}",
            result.DatasetsMovedCount, result.DatasetsFailedCount, result.SourceOrganizationDeleted);

        return result;
    }

    #region Member Management

    public bool IsMemberManagementEnabled => _accounts.Value.EnableOrganizationMemberManagement;

    public async Task<IEnumerable<OrganizationMemberDto>> GetMembers(string orgSlug, CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.Slug == orgSlug, ct);

        if (org == null)
            throw new NotFoundException($"Organization '{orgSlug}' not found");

        // Check if current user can view members
        var currentUser = await _authManager.GetCurrentUser();
        if (!await _authManager.IsUserAdmin() &&
            org.OwnerId != currentUser?.Id)
        {
            // Check if user is a member with sufficient permissions
            var userMembership = org.Users?.FirstOrDefault(u => u.UserId == currentUser?.Id);
            if (userMembership == null)
                throw new ForbiddenException("Access denied");
        }

        var members = new List<OrganizationMemberDto>();

        foreach (var orgUser in org.Users ?? Enumerable.Empty<OrganizationUser>())
        {
            var user = await _appContext.Users.FindAsync([orgUser.UserId], ct);
            if (user == null) continue;

            var grantedByUser = !string.IsNullOrEmpty(orgUser.GrantedBy)
                ? await _appContext.Users.FindAsync([orgUser.GrantedBy], ct)
                : null;

            members.Add(new OrganizationMemberDto
            {
                UserName = user.UserName,
                Email = user.Email,
                Permissions = orgUser.Permissions,
                GrantedAt = orgUser.GrantedAt,
                GrantedBy = grantedByUser?.UserName
            });
        }

        return members;
    }

    // The organization (with its members) whose members the current user is about to change: it must
    // exist, the user must be allowed to manage its members, and the feature must be on. The access
    // check comes before the feature check, so an anonymous caller gets 401 and a stranger 403 (D2)
    private async Task<(Organization Org, User CurrentUser)> GetOrganizationToManageMembers(string orgSlug,
        CancellationToken ct)
    {
        var org = await _memberships.GetWithMembersAsync(orgSlug, ct);

        var currentUser = await _authManager.GetCurrentUser();
        if (!await CanManageMembers(org, currentUser))
            throw new ForbiddenException("You don't have permission to manage members");

        if (!IsMemberManagementEnabled)
            throw new ForbiddenException("Organization member management is disabled");

        return (org, currentUser);
    }

    public async Task AddMember(string orgSlug, string userName, OrganizationPermissions permissions = OrganizationPermissions.ReadWrite,
        CancellationToken ct = default)
    {
        var (org, currentUser) = await GetOrganizationToManageMembers(orgSlug, ct);
        await _memberships.AddAsync(org, userName, permissions, currentUser, CancellationToken.None);
    }

    public async Task UpdateMemberPermission(string orgSlug, string userName, OrganizationPermissions permission,
        CancellationToken ct = default)
    {
        var (org, currentUser) = await GetOrganizationToManageMembers(orgSlug, ct);
        await _memberships.UpdateAsync(org, userName, permission, currentUser, CancellationToken.None);
    }

    public async Task RemoveMember(string orgSlug, string userName, CancellationToken ct = default)
    {
        var (org, currentUser) = await GetOrganizationToManageMembers(orgSlug, ct);
        await _memberships.RemoveAsync(org, userName, currentUser, CancellationToken.None);
    }

    private async Task<bool> CanManageMembers(Organization org, User user)
    {
        if (user == null) return false;

        // System admin can always manage
        if (await _authManager.IsUserAdmin()) return true;

        // Owner can always manage
        if (org.OwnerId == user.Id) return true;

        // Check if member has Admin permission
        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == user.Id);
        if (orgUser == null) return false;

        return orgUser.Permissions.CanManageMembers();
    }

    #endregion
}