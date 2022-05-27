using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class OrganizationsManager : IOrganizationsManager
    {
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;
        private readonly IUtils _utils;
        private readonly IDatasetsManager _datasetManager;
        private readonly ApplicationDbContext _appContext;
        private readonly ILogger<OrganizationsManager> _logger;

        // TODO: Add extensive logging
        // TODO: Add extensive testing
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
            var query = from org in _context.Organizations select org;

            if (!await _authManager.IsUserAdmin())
            {
                var currentUser = await _authManager.GetCurrentUser();

                if (currentUser == null)
                    throw new UnauthorizedException("Invalid user");

                query = query.Where(item => item.OwnerId == currentUser.Id);
            }
            
            // This can be optimized, but it's not a big deal because it's a cross database query anyway
            var usersMapper = await _appContext.Users.Select(item => new { item.Id, item.UserName })
                .ToDictionaryAsync(item => item.Id, item => item.UserName);

            return from org in query
                   let userName = org.OwnerId != null ? (usersMapper.ContainsKey(org.OwnerId) ? usersMapper[org.OwnerId] : null) : null
                   select new OrganizationDto
                   {
                       CreationDate = org.CreationDate,
                       Description = org.Description,
                       Slug = org.Slug,
                       Name = org.Name,
                       Owner = userName,
                       IsPublic = org.IsPublic
                   };
        }

        public async Task<OrganizationDto> Get(string orgSlug)
        {
            var org = await _utils.GetOrganization(orgSlug);

            return org.ToDto();
        }

        public async Task<OrganizationDto> AddNew(OrganizationDto organization, bool skipAuthCheck = false)
        {
            // TODO: To change when implementing anonymous users

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

            var org = await _utils.GetOrganization(orgSlug);

            // TODO: To change when implementing anonymous users
            var currentUser = await _authManager.GetCurrentUser();

            // NOTE: Is this a good idea? If activated there will be no way to change the public organization details
            // if (organization.Id == MagicStrings.PublicOrganizationSlug)
            //    return Unauthorized(new ErrorResponse("Cannot edit the public organization"));

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
                        throw new BadRequestException($"Cannot find user with orgSlug '{organization.Owner}'");

                }
            }

            org.IsPublic = organization.IsPublic;
            org.Name = organization.Name;
            org.Description = organization.Description;

            await _context.SaveChangesAsync();

        }

        public async Task Delete(string orgSlug)
        {

            var org = await _utils.GetOrganization(orgSlug);

            if (org == null)
                throw new NotFoundException("Cannot find organization with this orgSlug");

            if (!await _authManager.IsUserAdmin())
            {
                var currentUser = await _authManager.GetCurrentUser();

                // This seems a repeated check but it prevents the case in which a user tries to delete a public org without being the owner
                if (org.OwnerId != currentUser.Id)
                    throw new UnauthorizedException("The current user is not the owner of the organization");
            }
            else
            {
                if (org.Slug == MagicStrings.PublicOrganizationSlug)
                    throw new UnauthorizedException("Cannot remove the default public organization");
            }

            foreach (var ds in org.Datasets.ToArray())
            {

                // TODO: To check and re-check and re-check and re-check
                await _datasetManager.Delete(org.Slug, ds.Slug);
                _context.Datasets.Remove(ds);
            }

            _context.Organizations.Remove(org);
            await _context.SaveChangesAsync();
        }
    }
}