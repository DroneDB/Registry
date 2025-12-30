using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

public class OrganizationsManager : IOrganizationsManager
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IUtils _utils;
    private readonly IDatasetsManager _datasetManager;
    private readonly ApplicationDbContext _appContext;
    private readonly ILogger<OrganizationsManager> _logger;

    public OrganizationsManager(
        IAuthManager authManager,
        RegistryContext context,
        IUtils utils,
        IDatasetsManager datasetManager,
        ApplicationDbContext appContext,
        ILogger<OrganizationsManager> logger)
    {
        _authManager = authManager;
        _context = context;
        _utils = utils;
        _datasetManager = datasetManager;
        _appContext = appContext;
        _logger = logger;
    }

    public async Task<IEnumerable<OrganizationDto>> List()
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (!await _authManager.CanListOrganizations(currentUser))
            throw new UnauthorizedException("Invalid user");

        // Use projection to load only necessary data
        var organizationsQuery = _context.Organizations
            .AsNoTracking()
            .Where(org => org.OwnerId == currentUser.Id ||
                         org.Slug == MagicStrings.PublicOrganizationSlug ||
                         org.Users.Any(u => u.UserId == currentUser.Id))
            .Select(org => new OrganizationDto
            {
                CreationDate = org.CreationDate,
                Description = org.Description,
                Slug = org.Slug,
                Name = org.Name,
                IsPublic = org.IsPublic,
                Owner = org.OwnerId // Will be resolved to username later
            });

        var organizations = await organizationsQuery.ToListAsync();

        // Resolve owner names in a separate, more efficient query
        var ownerIds = organizations.Where(o => o.Owner != null)
                                   .Select(o => o.Owner)
                                   .Distinct()
                                   .ToArray();

        if (ownerIds.Length != 0)
        {
            var usersMapper = await _appContext.Users
                .AsNoTracking()
                .Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.UserName);

            // Update owner names
            foreach (var org in organizations.Where(org => org.Owner != null))
            {
                org.Owner = usersMapper.SafeGetValue(org.Owner);
            }
        }

        return organizations;
    }

    public async Task<IEnumerable<OrganizationDto>> ListPublic()
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

        var organizations = await organizationsQuery.ToListAsync();

        // Resolve owner names
        var ownerIds = organizations.Where(o => o.Owner != null)
                                   .Select(o => o.Owner)
                                   .Distinct()
                                   .ToArray();

        if (ownerIds.Length != 0)
        {
            var usersMapper = await _appContext.Users
                .AsNoTracking()
                .Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.UserName);

            foreach (var org in organizations.Where(org => org.Owner != null))
            {
                org.Owner = usersMapper.SafeGetValue(org.Owner);
            }
        }

        return organizations;
    }

    public async Task<OrganizationDto> Get(string orgSlug)
    {
        var org = _utils.GetOrganization(orgSlug);

        if (!await _authManager.RequestAccess(org, AccessType.Read))
            throw new UnauthorizedException("Invalid user");

        return org.ToDto();
    }

    public async Task<OrganizationDto> AddNew(OrganizationDto organization, bool skipAuthCheck = false)
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
                    throw new UnauthorizedException(
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
        org.CreationDate = DateTime.Now;

        await _context.Organizations.AddAsync(org);
        await _context.SaveChangesAsync();

        return org.ToDto();
    }

    public async Task Edit(string orgSlug, OrganizationDto organization)
    {
        var org = _utils.GetOrganization(orgSlug, withTracking: true);

        if (!await _authManager.RequestAccess(org, AccessType.Write))
            throw new UnauthorizedException("Invalid user");

        var currentUser = await _authManager.GetCurrentUser();

        if (!await _authManager.IsUserAdmin())
        {
            // If the owner is specified it should be the current user
            if (organization.Owner != null && organization.Owner != currentUser.Id)
                throw new UnauthorizedException("Cannot create a new organization that belongs to a different user");

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
                    throw new BadRequestException($"Cannot find user with id '{organization.Owner}'");
            }
        }

        org.OwnerId = organization.Owner;
        org.IsPublic = organization.IsPublic;
        org.Name = organization.Name;
        org.Description = organization.Description;

        await _context.SaveChangesAsync();
    }

    public async Task Delete(string orgSlug)
    {
        var org = _utils.GetOrganization(orgSlug, withTracking: true);

        if (!await _authManager.RequestAccess(org, AccessType.Delete))
            throw new UnauthorizedException("Invalid user");

        foreach (var ds in org.Datasets.ToArray())
        {
            // TODO: To check and re-check and re-check and re-check
            await _datasetManager.Delete(org.Slug, ds.Slug);
        }

        _context.Organizations.Remove(org);
        await _context.SaveChangesAsync();
    }

    public async Task<MergeOrganizationResultDto> Merge(
        string sourceOrgSlug,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict,
        bool deleteSourceOrganization = true)
    {
        // Only admins can perform this operation
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only administrators can merge organizations");

        if (string.IsNullOrWhiteSpace(sourceOrgSlug))
            throw new BadRequestException("Source organization slug is required");

        if (string.IsNullOrWhiteSpace(destOrgSlug))
            throw new BadRequestException("Destination organization slug is required");

        if (sourceOrgSlug == destOrgSlug)
            throw new BadRequestException("Source and destination organizations cannot be the same");

        // Validate public organization cannot be merged
        if (sourceOrgSlug == MagicStrings.PublicOrganizationSlug)
            throw new BadRequestException("Cannot merge the public organization");

        var sourceOrg = _utils.GetOrganization(sourceOrgSlug, withTracking: true);
        var destOrg = _utils.GetOrganization(destOrgSlug, withTracking: true);

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
                conflictResolution);

            result.DatasetResults = moveResults.ToArray();
            result.DatasetsMovedCount = result.DatasetResults.Count(r => r.Success);
            result.DatasetsFailedCount = result.DatasetResults.Count(r => !r.Success);
        }
        else
        {
            result.DatasetResults = [];
            result.DatasetsMovedCount = 0;
            result.DatasetsFailedCount = 0;
        }

        // Transfer OrganizationUsers to destination (avoiding duplicates)
        var sourceUsers = sourceOrg.Users?.ToList() ?? [];
        var destUserIds = destOrg.Users?.Select(u => u.UserId).ToHashSet() ?? [];

        foreach (var sourceUser in sourceUsers)
        {
            if (destUserIds.Contains(sourceUser.UserId)) continue;

            // Add user to destination organization
            var newOrgUser = new OrganizationUser
            {
                Organization = destOrg,
                OrganizationSlug = destOrgSlug,
                UserId = sourceUser.UserId
            };
            destOrg.Users ??= new List<OrganizationUser>();
            destOrg.Users.Add(newOrgUser);
            _logger.LogInformation("Transferred user '{UserId}' from '{SourceOrgSlug}' to '{DestOrgSlug}'",
                sourceUser.UserId, sourceOrgSlug, destOrgSlug);
        }

        await _context.SaveChangesAsync();

        // Delete source organization if requested and all datasets were moved successfully
        if (deleteSourceOrganization)
        {
            if (result.DatasetsFailedCount == 0)
            {
                _logger.LogInformation("Deleting source organization '{SourceOrgSlug}' after successful merge", sourceOrgSlug);

                // Reload the organization to ensure we have the latest state
                sourceOrg = _utils.GetOrganization(sourceOrgSlug, withTracking: true);

                // Clear remaining users
                sourceOrg.Users?.Clear();

                _context.Organizations.Remove(sourceOrg);
                await _context.SaveChangesAsync();

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
}