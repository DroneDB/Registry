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
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers
{

    [Authorize]
    [ApiController]
    [Route("ddb/{orgId:alpha}/ds")]
    public class DatasetsController : ControllerBaseEx
    {
        private readonly IDatasetsManager _datasetsManager;

        public DatasetsController(IDatasetsManager datasetsManager)
        {
            _datasetsManager = datasetsManager;
        }

        [HttpGet(Name = nameof(DatasetsController) + "." + nameof(GetAll))]
        public async Task<IActionResult> GetAll([FromRoute] string orgId)
        {
            try
            {
                return Ok(await _datasetsManager.GetAll(orgId));
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        [HttpGet("{id}", Name = nameof(DatasetsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, string id)
        {
            try
            {
                return Ok(await _datasetsManager.Get(orgId, id));
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }


        [HttpPost]
        public async Task<IActionResult> Post([FromRoute] string orgId, [FromBody] DatasetDto dataset)
        {
            try
            {
                var newDs = await _datasetsManager.AddNew(orgId, dataset);
                return CreatedAtRoute(nameof(DatasetsController) + "." + nameof(Get), new { id = newDs.Id },
                    newDs);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        // POST: ddb/
        [HttpPut("{id}")]
        public async Task<IActionResult> Put([FromRoute] string orgId, string id, [FromBody] DatasetDto dataset)
        {

            try
            {
                await _datasetsManager.Edit(orgId, id, dataset);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        // DELETE: ddb/id
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete([FromRoute] string orgId, string id)
        {

            try
            {
                await _datasetsManager.Delete(orgId, id);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }


    }
}
