using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("ddb")]
    public class OrganizationsController : ControllerBaseEx
    {
        private readonly IOptions<AppSettings> _appSettings;
        private readonly UserManager<User> _usersManager;
        private readonly RegistryContext _context;
        private readonly IUtils _utils;
        private readonly IDatasetManager _datasetManager;

        public OrganizationsController(
            IOptions<AppSettings> appSettings,
            UserManager<User> usersManager,
            RegistryContext context,
            IUtils utils,
            IDatasetManager datasetManager) : base(usersManager)
        {
            _appSettings = appSettings;
            _usersManager = usersManager;
            _context = context;
            _utils = utils;
            _datasetManager = datasetManager;
            
        }

        // GET: ddb/
        [HttpGet(Name = nameof(GetAll))]
        public async Task<IActionResult> GetAll()
        {
            var query = from org in _context.Organizations select org;

            if (!await IsUserAdmin())
            {
                var currentUser = await GetCurrentUser();

                if (currentUser == null)
                    return Unauthorized(new ErrorResponse("Invalid user"));

                query = query.Where(item => item.OwnerId == currentUser.Id || item.OwnerId == null || item.IsPublic);
            }

            return Ok(from org in query
                   select new OrganizationDto
                   {
                       CreationDate = org.CreationDate,
                       Description = org.Description,
                       Id = org.Id,
                       Name = org.Name,
                       Owner = org.OwnerId,
                       IsPublic = org.IsPublic
                   });
        }

        // GET: ddb/
        [HttpGet("{id}", Name = nameof(Get))]
        public async Task<IActionResult> Get(string id)
        {
            var query = from org in _context.Organizations
                        where org.Id == id
                        select org;

            if (!await IsUserAdmin())
            {
                var currentUser = await GetCurrentUser();

                if (currentUser == null)
                    return Unauthorized(new ErrorResponse("Invalid user"));

                query = query.Where(item => item.OwnerId == currentUser.Id || item.IsPublic || item.OwnerId == null);
            }

            var res = query.FirstOrDefault();

            if (res == null) return NotFound(new ErrorResponse("Organization not found"));

            return Ok(new OrganizationDto(res));
        }

        // POST: ddb/
        [HttpPost]
        public async Task<ActionResult<OrganizationDto>> Post([FromBody] OrganizationDto organization)
        {

            if (!_utils.IsOrganizationNameValid(organization.Id))
                return BadRequest(new ErrorResponse("Invalid organization id"));

            var existingOrg = _context.Organizations.FirstOrDefault(item => item.Id == organization.Id);

            if (existingOrg != null)
                return Conflict(new ErrorResponse("The organization already exists"));

            var currentUser = await GetCurrentUser();

            if (currentUser == null)
                return Unauthorized(new ErrorResponse("Invalid user"));

            if (!await IsUserAdmin())
            {

                // If the owner is specified it should be the current user
                if (organization.Owner != null && organization.Owner != currentUser.Id)
                    return Unauthorized(new ErrorResponse("Cannot create a new organization that belongs to a different user"));

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
                    var user = await _usersManager.FindByIdAsync(organization.Owner);

                    if (user == null)
                        return BadRequest(new ErrorResponse($"Cannot find user with id '{organization.Owner}'"));

                }
            }

            var org = organization.ToEntity();
            org.CreationDate = DateTime.Now;

            await _context.Organizations.AddAsync(org);
            await _context.SaveChangesAsync();

            return CreatedAtRoute(nameof(Get), new { id = org.Id }, org);

        }
        
        // POST: ddb/
        [HttpPut("{id}")]
        public async Task<ActionResult<OrganizationDto>> Put(string id, [FromBody] OrganizationDto organization)
        {

            if (id != organization.Id)
                return BadRequest(new ErrorResponse("Ids don't match"));
            
            if (!_utils.IsOrganizationNameValid(organization.Id))
                return BadRequest(new ErrorResponse("Invalid organization id"));

            var existingOrg = _context.Organizations.FirstOrDefault(item => item.Id == id);

            if (existingOrg == null)
                return NotFound(new ErrorResponse("Cannot find organization with this id"));

            // NOTE: Is this a good idea? If activated there will be no way to change the public organization details
            // if (organization.Id == MagicStrings.PublicOrganizationId)
            //    return Unauthorized(new ErrorResponse("Cannot edit the public organization"));

            var currentUser = await GetCurrentUser();

            if (currentUser == null)
                return Unauthorized(new ErrorResponse("Invalid user"));

            if (!await IsUserAdmin())
            {

                // If the owner is specified it should be the current user
                if (organization.Owner != null && organization.Owner != currentUser.Id)
                    return Unauthorized(new ErrorResponse("Cannot create a new organization that belongs to a different user"));

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
                    var user = await _usersManager.FindByIdAsync(organization.Owner);

                    if (user == null)
                        return BadRequest(new ErrorResponse($"Cannot find user with id '{organization.Owner}'"));

                }
            }

            existingOrg.IsPublic = organization.IsPublic;
            existingOrg.Name = organization.Name;
            existingOrg.Description = organization.Description;

            await _context.SaveChangesAsync();

            return NoContent();

        }

        // DELETE: ddb/id
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            if (!_utils.IsOrganizationNameValid(id))
                return BadRequest(new ErrorResponse("Invalid organization id"));

            var org = _context.Organizations.Include(item => item.Datasets)
                .FirstOrDefault(item => item.Id == id);

            if (org == null)
                return NotFound(new ErrorResponse("Cannot find organization with this id"));

            var currentUser = await GetCurrentUser();

            if (currentUser == null)
                return Unauthorized(new ErrorResponse("Invalid user"));
            
            if (!await IsUserAdmin())
            {
                if (org.OwnerId != currentUser.Id)
                    return Unauthorized(new ErrorResponse("The current user is not the owner of the organization"));
            }
            else
            {
                if (org.Id == MagicStrings.PublicOrganizationId)
                    return Unauthorized(new ErrorResponse("Cannot remove the default public organization"));
            }

            foreach (var ds in org.Datasets.ToArray()) {

                _datasetManager.RemoveDataset(ds.Id);
                _context.Datasets.Remove(ds);
            }

            _context.Organizations.Remove(org);
            await _context.SaveChangesAsync();

            return NoContent();
        }

    }
}
