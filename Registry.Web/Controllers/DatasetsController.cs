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

namespace Registry.Web.Controllers
{

    [Authorize]
    [ApiController]
    [Route("ddb/{orgId:alpha}/ds")]
    public class DatasetsController : ControllerBaseEx
    {
        private readonly IOptions<AppSettings> _appSettings;
        private readonly UserManager<User> _usersManager;
        private readonly RegistryContext _context;

        public DatasetsController(IOptions<AppSettings> appSettings, UserManager<User> usersManager, RegistryContext context) : base(usersManager)
        {
            _appSettings = appSettings;
            _usersManager = usersManager;
            _context = context;
        }

        [HttpGet(Name = nameof(DatasetsController) + "." + nameof(GetAll))]
        public async Task<IActionResult> GetAll([FromRoute] string orgId)
        {
            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                return NotFound(new ErrorResponse("Organization not found"));

            var currentUser = await GetCurrentUser();

            if (currentUser == null)
                return Unauthorized(new ErrorResponse("Invalid user"));

            if (!await IsUserAdmin() && !(currentUser.Id == org.OwnerId || org.OwnerId == null))
                return Unauthorized(new ErrorResponse("This organization does not belong to the current user"));


            var query = from ds in org.Datasets

                        select new DatasetDto
                        {
                            Id = ds.Id,
                            Slug = ds.Slug,
                            CreationDate = ds.CreationDate,
                            Description = ds.Description,
                            LastEdit = ds.LastEdit,
                            Name = ds.Name,
                            License = ds.License,
                            Meta = ds.Meta,
                            ObjectsCount = ds.ObjectsCount,
                            Size = ds.Size
                        };

            return Ok(query);
        }

        [HttpGet("{id}", Name = nameof(DatasetsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, string id)
        {
            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                return NotFound(new ErrorResponse("Organization not found"));

            var currentUser = await GetCurrentUser();

            if (currentUser == null)
                return Unauthorized(new ErrorResponse("Invalid user"));

            if (!await IsUserAdmin() && !(currentUser.Id == org.OwnerId || org.OwnerId == null))
                return Unauthorized(new ErrorResponse("This organization does not belong to the current user"));

            var ds = org.Datasets.FirstOrDefault(item => item.Slug == id);

            if (ds == null)
                return NotFound(new ErrorResponse("Cannot find dataset"));

            return Ok(new DatasetDto(ds));
        }

        /*
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] DatasetDto dataset)
        {
            return null;
        }*/


    }
}
