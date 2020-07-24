using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

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
        private readonly RoleManager<IdentityRole> _roleManager;

        public OrganizationsController(
            IOptions<AppSettings> appSettings,
            UserManager<User> usersManager,
            RegistryContext context,
            RoleManager<IdentityRole> roleManager) : base(usersManager)
        {
            _appSettings = appSettings;
            _usersManager = usersManager;
            _context = context;
            _roleManager = roleManager;

            // If no organizations in database, let's create the public one
            if (!_context.Organizations.Any())
            {
                var entity = new Organization
                {
                    Id = "public",
                    Name = "Public",
                    CreationDate = DateTime.Now,
                    Description = "Public organization",
                    IsPublic = true,
                    // I don't think this is a good idea
                    OwnerId = usersManager.Users.First().Id
                };
                var ds = new Dataset
                {
                    Id = "default",
                    Name = "Default",
                    Description = "Default dataset",
                    IsPublic = true,
                    CreationDate = DateTime.Now,
                    LastEdit = DateTime.Now
                };
                entity.Datasets = new List<Dataset> { ds };

                _context.Organizations.Add(entity);
                _context.SaveChanges();
            }

        }

        // GET: ddb/
        [HttpGet(Name = "GetAll")]
        public async Task<IQueryable<OrganizationDto>> Get()
        {
            var query = from org in _context.Organizations select org;

            if (!await IsUserAdmin())
            {
                var currentUser = await GetCurrentUser();
                query = query.Where(item => item.Id == currentUser.Id || item.IsPublic);
            }

            return from org in query
                   select new OrganizationDto
                   {
                       CreationDate = org.CreationDate,
                       Description = org.Description,
                       Id = org.Id,
                       Name = org.Name,
                       Owner = org.OwnerId,
                       IsPublic = org.IsPublic
                   };
        }

        // This is not triggered but instead DatasetsController

        // GET: ddb/
        [HttpGet("ddb/{id:alpha}",Name = "Get")]
        public async Task<IActionResult> Get(string id)
        {
            var query = from org in _context.Organizations
                        where org.Id == id
                        select org;

            if (!await IsUserAdmin())
            {
                var currentUser = await GetCurrentUser();
                query = query.Where(item => item.Id == currentUser.Id || item.IsPublic);
            }

            var res = query.FirstOrDefault();

            if (res == null) return NotFound();

            return Ok(new OrganizationDto(res));
        }

        // POST: ddb/
        [HttpPost]
        public async Task<ActionResult<OrganizationDto>> Post([FromBody] OrganizationDto organization)
        {

            if (!await IsUserAdmin())
            {
                var currentUser = await GetCurrentUser();

                if (organization.Owner != null && organization.Owner != currentUser.Id)
                    return Unauthorized();

                // The current user is the owner
                organization.Owner = currentUser.Id;

            }

            var org = organization.ToEntity();
            org.CreationDate = DateTime.Now;

            await _context.Organizations.AddAsync(org);
            await _context.SaveChangesAsync();

            return CreatedAtRoute("Get", new { org.Id }, org);

        }

    }
}
