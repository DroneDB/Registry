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
    public class DatasetsController : ControllerBase
    {
        private readonly IOptions<AppSettings> _appSettings;
        private readonly UserManager<User> _usersManager;
        private readonly RegistryContext _context;

        public DatasetsController(IOptions<AppSettings> appSettings, UserManager<User> usersManager, RegistryContext context)
        {
            _appSettings = appSettings;
            _usersManager = usersManager;
            _context = context;
        }

        // The dataset should have a unique ID in the DB plus a unique slug inside the organization. Because
        // Because multiple organizations could have datasets with the same dataset
        [HttpGet]
        public IActionResult Get([FromRoute]string orgId)
        {
            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                return StatusCode(404, new ErrorResponse("Organization not found"));

            var query = from ds in org.Datasets

                select new DatasetDto
                {
                    Id = ds.Id,
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


    }
}
