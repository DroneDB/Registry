using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Organizations;

/// <summary>Creating, editing and deleting organizations (out of <c>OrganizationsManager</c>).</summary>
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

    private const string SlugTakenMessage = "The organization already exists";

    /// <summary>Creates an organization: any signed-in user for itself, an administrator for any existing user.</summary>
    public async Task<OrganizationDto> AddNew(OrganizationRequestDto organization, bool skipAuthCheck = false,
        CancellationToken ct = default)
    {
        // A name with no Latin letter or digit needs a slug the request gives (D-035); the refusal is the name's, at its field
        InputRules.EnsureLatinName(organization.Name, organization.Slug, "name");

        // Without the auth check the request's owner is taken as it is (an organization may have no owner)
        string? ownerId;

        if (!skipAuthCheck)
        {
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            if (!SlugRules.IsValid(organization.Slug))
                throw new BadRequestException(SlugRules.InvalidMessage("Organization"));

            if (await _context.Organizations.AnyAsync(item => item.Slug == organization.Slug, ct))
                throw new ConflictException(SlugTakenMessage)
                {
                    // What must be unique is the slug: the form shows the refusal there (D-047, FR-ORG-04)
                    FieldErrors = new Dictionary<string, string[]> { ["slug"] = [SlugTakenMessage] }
                };

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
            if (!SlugRules.IsValid(organization.Slug))
                throw new BadRequestException(SlugRules.InvalidMessage("Organization"));

            ownerId = organization.Owner;
        }

        var org = organization.ToEntity(ownerId, DateTime.UtcNow);

        // Honoured until the organization is written
        ct.ThrowIfCancellationRequested();
        await _context.Organizations.AddAsync(org, CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);

        return await _queries.ToDtoWithOwnerNameAsync(org);
    }

    /// <summary>Edits an organization (write access); only an administrator changes its owner.</summary>
    public async Task Edit(string orgSlug, OrganizationRequestDto organization, CancellationToken ct = default)
    {
        var org = await _access.ResolveOrganizationAsync(orgSlug, AccessType.Write, withTracking: true, ct: ct);

        // D-035: a name the edit keeps is not checked again (an organization named 'Москва' with the slug 'moskva' keeps
        // its description and visibility editable); a new name has the slug of the organization, which an edit keeps
        if (!string.Equals(organization.Name, org.Name, StringComparison.Ordinal))
            InputRules.EnsureLatinName(organization.Name, org.Slug, "name");

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
        // Honoured while the organization is looked up; its datasets are then deleted to the end
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
