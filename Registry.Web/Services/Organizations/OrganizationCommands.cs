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
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Organizations;

/// <summary>Creating, editing and deleting organizations (RQ-12, out of <c>OrganizationsManager</c>).</summary>
public sealed class OrganizationCommands
{
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IDatasetsManager _datasetManager;
    private readonly OrganizationQueries _queries;

    public OrganizationCommands(IAuthManager authManager, IDatasetAccessResolver access, RegistryContext context,
        IDatasetResolver datasets, IDatasetsManager datasetManager, OrganizationQueries queries)
    {
        _authManager = authManager;
        _access = access;
        _context = context;
        _datasets = datasets;
        _datasetManager = datasetManager;
        _queries = queries;
    }

    /// <summary>Creates the private organization a new user gets, named after the user (no permission check: the caller is the sign-up).</summary>
    public async Task<OrganizationDto> AddUserDefaultAsync(string userId, string userName, CancellationToken ct = default) =>
        await AddNew(new OrganizationRequestDto
        {
            Name = userName,
            IsPublic = false,
            Owner = userId,
            Slug = await _datasets.GetFreeOrganizationSlugAsync(userName, ct)
        }, skipAuthCheck: true, ct);

    /// <summary>Creates an organization: any signed-in user for itself, an administrator for any existing user.</summary>
    public async Task<OrganizationDto> AddNew(OrganizationRequestDto organization, bool skipAuthCheck = false,
        CancellationToken ct = default)
    {
        string ownerId;

        if (!skipAuthCheck)
        {
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            if (!organization.Slug.IsValidSlug())
                throw new BadRequestException(SlugRules.InvalidMessage("Organization"));

            if (await _context.Organizations.AnyAsync(item => item.Slug == organization.Slug, ct))
                throw new ConflictException("The organization already exists");

            if (!await _authManager.IsUserAdmin())
            {
                // If the owner is specified it should be the current user
                if (organization.Owner != null && organization.Owner != currentUser.Id)
                    throw new ForbiddenException(
                        "Cannot create a new organization that belongs to a different user");

                // The current user is the owner
                ownerId = currentUser.Id;
            }
            else if (organization.Owner == null)
            {
                // If no owner specified, the owner is the current user
                ownerId = currentUser.Id;
            }
            else
            {
                // Otherwise check if user exists
                if (!await _authManager.UserExists(organization.Owner))
                    throw new BadRequestException($"Cannot find user with name '{organization.Owner}'");

                ownerId = organization.Owner;
            }
        }
        else
        {
            if (!organization.Slug.IsValidSlug())
                throw new BadRequestException(SlugRules.InvalidMessage("Organization"));

            ownerId = organization.Owner;
        }

        var org = organization.ToEntity(ownerId, DateTime.UtcNow);

        // Honoured until the organization is written (RQ-13)
        ct.ThrowIfCancellationRequested();
        await _context.Organizations.AddAsync(org, CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);

        return await _queries.ToDtoWithOwnerNameAsync(org);
    }

    /// <summary>Edits an organization (write access); only an administrator changes its owner.</summary>
    public async Task Edit(string orgSlug, OrganizationRequestDto organization, CancellationToken ct = default)
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

    /// <summary>Deletes an organization and its datasets (delete access).</summary>
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
}
