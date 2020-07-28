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
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("ddb")]
    public class OrganizationsController : ControllerBaseEx
    {
        private readonly IOrganizationsManager _organizationsManager;

        public OrganizationsController(IOrganizationsManager organizationsManager)
        {
            _organizationsManager = organizationsManager;
        }

        // GET: ddb/
        [HttpGet(Name = nameof(OrganizationsController) + "." + nameof(GetAll))]
        public async Task<IActionResult> GetAll()
        {
            try
            {
                return Ok(await _organizationsManager.GetAll());
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        // GET: ddb/id
        [HttpGet("{id}", Name = nameof(OrganizationsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get(string id)
        {
            try
            {
                return Ok(await _organizationsManager.Get(id));
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        // POST: ddb/
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] OrganizationDto organization)
        {

            try
            {
                var newOrg = await _organizationsManager.AddNew(organization);
                return CreatedAtRoute(nameof(OrganizationsController) + "." + nameof(Get), new {id = newOrg.Id},
                    newOrg);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        // POST: ddb/
        [HttpPut("{id}")]
        public async Task<IActionResult> Put(string id, [FromBody] OrganizationDto organization)
        {
            
            try
            {
                await _organizationsManager.Edit(id, organization);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        // DELETE: ddb/id
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {

            try
            {
                await _organizationsManager.Delete(id);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

    }
}
