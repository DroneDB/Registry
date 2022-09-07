using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
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

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");
            
            var query = 
                from org in _context.Organizations
                where org.OwnerId == currentUser.Id || org.Slug == MagicStrings.PublicOrganizationSlug
                select org;
            
            // This can be optimized, but it's not a big deal because it's a cross database query anyway
            var usersMapper = await _appContext.Users.Select(item => new { item.Id, item.UserName })
                .ToDictionaryAsync(item => item.Id, item => item.UserName);

            return from org in query
                   let userName = org.OwnerId != null ? usersMapper.SafeGetValue(org.OwnerId) : null
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

            var org = _utils.GetOrganization(orgSlug);
            
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

            var org = _utils.GetOrganization(orgSlug);

            if (!await _authManager.RequestAccess(org, AccessType.Delete))
                throw new UnauthorizedException("Invalid user");

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