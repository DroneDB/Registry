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
    public class OrganizationsController : ControllerBase
    {
        private readonly IOptions<AppSettings> _appSettings;
        private readonly UserManager<User> _usersManager;
        private readonly RegistryContext _context;

        public OrganizationsController(IOptions<AppSettings> appSettings, UserManager<User> usersManager, RegistryContext context)
        {
            _appSettings = appSettings;
            _usersManager = usersManager;
            _context = context;

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

        [HttpGet]
        public IQueryable<OrganizationDto> Get()
        {
            var query = from org in _context.Organizations
                        select new OrganizationDto
                        {
                            CreationDate = org.CreationDate,
                            Description = org.Description,
                            Id = org.Id,
                            Name = org.Name
                        };

            return query;
        }


    }
}
