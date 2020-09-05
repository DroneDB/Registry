﻿using System;
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

        // GET: ddb/orgSlug
        [HttpGet("{orgSlug:regex([[\\w-]]+)}", Name = nameof(OrganizationsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get(string orgSlug)
        {
            try
            {
                _logger.LogDebug($"Organizations controller Get('{orgSlug}')");

                return Ok(await _organizationsManager.Get(orgSlug));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Get('{orgSlug}')");

                return ExceptionResult(ex);
            }

        }

        // POST: ddb/
        [HttpPost]
        public async Task<IActionResult> Post([FromForm] OrganizationDto organization)
        {

            try
            {
                _logger.LogDebug($"Organizations controller Post('{organization?.Slug}')");

                var newOrg = await _organizationsManager.AddNew(organization);
                return CreatedAtRoute(nameof(OrganizationsController) + "." + nameof(Get), new { orgSlug = newOrg.Slug },
                    newOrg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Post('{organization?.Slug}')");

                return ExceptionResult(ex);
            }

        }

        // POST: ddb/
        [HttpPut("{orgSlug:regex([[\\w-]]+)}")]
        public async Task<IActionResult> Put(string orgSlug, [FromForm] OrganizationDto organization)
        {

            try
            {
                _logger.LogDebug($"Organizations controller Put('{orgSlug}', {organization?.Slug}')");

                await _organizationsManager.Edit(orgSlug, organization);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Put('{orgSlug}', {organization?.Slug}')");

                return ExceptionResult(ex);
            }

        }

        // DELETE: ddb/orgSlug
        [HttpDelete("{orgSlug:regex([[\\w-]]+)}")]
        public async Task<IActionResult> Delete(string orgSlug)
        {

            try
            {
                _logger.LogDebug($"Organizations controller Delete('{orgSlug}')");

                await _organizationsManager.Delete(orgSlug);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Organizations controller Delete('{orgSlug}')");

                return ExceptionResult(ex);
            }

        }

    }
}