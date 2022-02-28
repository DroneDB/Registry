using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [ApiController]
    [Route(RoutesHelper.OrganizationsRadix)]
    public class OrganizationsController : ControllerBaseEx
    {
        private readonly IOrganizationsManager _organizationsManager;
        private readonly ILogger<OrganizationsController> _logger;

        public OrganizationsController(IOrganizationsManager organizationsManager, ILogger<OrganizationsController> logger)
        {
            _organizationsManager = organizationsManager;
            _logger = logger;
        }

        [Authorize]
        [HttpGet(Name = nameof(OrganizationsController) + "." + nameof(GetAll))]
        [ProducesResponseType(typeof(IEnumerable<OrganizationDto>), 200)]
        public async Task<IActionResult> GetAll()
        {
            try
            {
                _logger.LogDebug("Organizations controller GetAll()");

                return Ok(await _organizationsManager.List());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Organizations controller GetAll()");

                return ExceptionResult(ex);
            }
        }

        [HttpGet(RoutesHelper.OrganizationSlug, Name = nameof(OrganizationsController) + "." + nameof(Get))]
        [ProducesResponseType(typeof(OrganizationDto), 200)]
        public async Task<IActionResult> Get(string orgSlug)
        {
            try
            {
                _logger.LogDebug("Organizations controller Get('{OrgSlug}')", orgSlug);

                return Ok(await _organizationsManager.Get(orgSlug));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Organizations controller Get('{OrgSlug}')", orgSlug);

                return ExceptionResult(ex);
            }

        }

        [HttpPost]
        [ProducesResponseType(typeof(OrganizationDto), 201)]
        public async Task<IActionResult> Post([FromForm] OrganizationDto organization)
        {

            try
            {
                _logger.LogDebug("Organizations controller Post('{organization?.Slug}')", organization?.Slug);

                var newOrg = await _organizationsManager.AddNew(organization);
                return CreatedAtRoute(nameof(OrganizationsController) + "." + nameof(Get), new { orgSlug = newOrg.Slug },
                    newOrg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Organizations controller Post('{organization?.Slug}')", organization?.Slug);

                return ExceptionResult(ex);
            }

        }

        [HttpPut(RoutesHelper.OrganizationSlug)]
        [ProducesResponseType(204)]
        public async Task<IActionResult> Put(string orgSlug, [FromForm] OrganizationDto organization)
        {

            try
            {
                _logger.LogDebug("Organizations controller Put('{OrgSlug}', {organization?.Slug}')",orgSlug, organization?.Slug);

                await _organizationsManager.Edit(orgSlug, organization);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Organizations controller Put('{OrgSlug}', {organization?.Slug}')", orgSlug, organization?.Slug);

                return ExceptionResult(ex);
            }

        }

        [HttpDelete(RoutesHelper.OrganizationSlug)]
        [ProducesResponseType(204)]
        public async Task<IActionResult> Delete(string orgSlug)
        {

            try
            {
                _logger.LogDebug("Organizations controller Delete('{OrgSlug}')", orgSlug);

                await _organizationsManager.Delete(orgSlug);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Organizations controller Delete('{OrgSlug}')", orgSlug);

                return ExceptionResult(ex);
            }

        }

    }
}
