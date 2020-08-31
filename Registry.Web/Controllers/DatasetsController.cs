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
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers
{

    [Authorize]
    [ApiController]
    [Route("ddb/{orgId:regex([[\\w-]]+)}/ds")]
    public class DatasetsController : ControllerBaseEx
    {
        private readonly IDatasetsManager _datasetsManager;
        private readonly IShareManager _shareManager;
        private readonly ILogger<DatasetsController> _logger;

        public DatasetsController(IDatasetsManager datasetsManager, IShareManager shareManager, ILogger<DatasetsController> logger)
        {
            _datasetsManager = datasetsManager;
            _shareManager = shareManager;
            _logger = logger;
        }

        
        [HttpGet("{dsId:regex([[\\w-]]+)}/batches")]
        public async Task<IActionResult> Batches([FromRoute] string orgId, string dsId)
        {
            try
            {
                _logger.LogDebug($"Dataset controller Batches('{orgId}', '{dsId}')");

                var lst = await _shareManager.ListBatches(orgId, dsId);

                return Ok(lst);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Dataset controller Batches('{orgId}', '{dsId}')");

                return ExceptionResult(ex);
            }
        }

        [HttpGet(Name = nameof(DatasetsController) + "." + nameof(GetAll))]
        public async Task<IActionResult> GetAll([FromRoute] string orgId)
        {
            try
            {
                _logger.LogDebug($"Dataset controller GetAll('{orgId}')");
                return Ok(await _datasetsManager.List(orgId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Dataset controller GetAll('{orgId}')");
                return ExceptionResult(ex);
            }
        }

        [HttpGet("{id:regex([[\\w-]]+)}", Name = nameof(DatasetsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, string id)
        {
            try
            {
                _logger.LogDebug($"Dataset controller Get('{orgId}', '{id}')");

                return Ok(await _datasetsManager.Get(orgId, id));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Dataset controller Get('{orgId}', '{id}')");
                return ExceptionResult(ex);
            }
        }


        [HttpPost]
        public async Task<IActionResult> Post([FromRoute] string orgId, [FromForm] DatasetDto dataset)
        {
            try
            {
                _logger.LogDebug($"Dataset controller Post('{orgId}', '{dataset?.Slug}')");

                var newDs = await _datasetsManager.AddNew(orgId, dataset);
                return CreatedAtRoute(nameof(DatasetsController) + "." + nameof(Get), new { id = newDs.Id },
                    newDs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Dataset controller Post('{orgId}', '{dataset?.Slug}')");
                return ExceptionResult(ex);
            }
        }

        // POST: ddb/
        [HttpPut("{id:regex([[\\w-]]+)}")]
        public async Task<IActionResult> Put([FromRoute] string orgId, string id, [FromForm] DatasetDto dataset)
        {

            try
            {
                _logger.LogDebug($"Dataset controller Put('{orgId}', '{id}', '{dataset?.Slug}')");

                await _datasetsManager.Edit(orgId, id, dataset);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Dataset controller Put('{orgId}', '{id}', '{dataset?.Slug}')");
                return ExceptionResult(ex);
            }

        }

        // DELETE: ddb/id
        [HttpDelete("{id:regex([[\\w-]]+)}")]
        public async Task<IActionResult> Delete([FromRoute] string orgId, string id)
        {

            try
            {
                _logger.LogDebug($"Dataset controller Delete('{orgId}', '{id}')");

                await _datasetsManager.Delete(orgId, id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Dataset controller Delete('{orgId}', '{id}')");

                return ExceptionResult(ex);
            }

        }


    }
}
