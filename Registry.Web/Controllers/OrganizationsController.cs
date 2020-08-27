using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<OrganizationsController> _logger;

        public OrganizationsController(IOrganizationsManager organizationsManager, ILogger<OrganizationsController> _logger)
        {
            _organizationsManager = organizationsManager;
            this._logger = _logger;
        }

        // GET: ddb/
        [HttpGet(Name = nameof(OrganizationsController) + "." + nameof(GetAll))]
        public async Task<IActionResult> GetAll()
        {
            try
            {
                _logger.LogDebug($"Organizations controller GetAll()");

                return Ok(await _organizationsManager.List());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller GetAll()");

                return ExceptionResult(ex);
            }
        }

        // GET: ddb/id
        [HttpGet("{id}", Name = nameof(OrganizationsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get(string id)
        {
            try
            {
                _logger.LogDebug($"Organizations controller Get('{id}')");

                return Ok(await _organizationsManager.Get(id));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Get('{id}')");

                return ExceptionResult(ex);
            }

        }

        // POST: ddb/
        [HttpPost]
        public async Task<IActionResult> Post([FromForm] OrganizationDto organization)
        {

            try
            {
                _logger.LogDebug($"Organizations controller Post('{organization?.Id}')");

                var newOrg = await _organizationsManager.AddNew(organization);
                return CreatedAtRoute(nameof(OrganizationsController) + "." + nameof(Get), new {id = newOrg.Id},
                    newOrg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Post('{organization?.Id}')");

                return ExceptionResult(ex);
            }

        }

        // POST: ddb/
        [HttpPut("{id}")]
        public async Task<IActionResult> Put(string id, [FromForm] OrganizationDto organization)
        {
            
            try
            {
                _logger.LogDebug($"Organizations controller Put('{id}', {organization?.Id}')");

                await _organizationsManager.Edit(id, organization);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Put('{id}', {organization?.Id}')");

                return ExceptionResult(ex);
            }

        }

        // DELETE: ddb/id
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {

            try
            {
                _logger.LogDebug($"Organizations controller Delete('{id}')");

                await _organizationsManager.Delete(id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Delete('{id}')");

                return ExceptionResult(ex);
            }

        }

    }
}
