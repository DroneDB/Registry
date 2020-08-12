﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    [Route("ddb/{orgId:alpha}/ds/{dsId:alpha}")]
    public class ObjectsController : ControllerBaseEx
    {
        private readonly IObjectsManager _objectsManager;
        private readonly ILogger<ObjectsController> _logger;

        public ObjectsController(IObjectsManager datasetsManager, ILogger<ObjectsController> logger)
        {
            _objectsManager = datasetsManager;
            _logger = logger;
        }
        
        [HttpGet("obj/{**path}", Name = nameof(ObjectsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {
            try
            {
                _logger.LogDebug($"Objects controller Get('{orgId}', '{dsId}', '{path}')");

                var res = await _objectsManager.Get(orgId, dsId, path);
                return File(res.Data, res.ContentType, res.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Get('{orgId}', '{dsId}', '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpGet("info/{**path}", Name = nameof(ObjectsController) + "." + nameof(GetInfo))]
        public async Task<IActionResult> GetInfo([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {
            try
            {
                _logger.LogDebug($"Objects controller GetInfo('{orgId}', '{dsId}', '{path}')");

                var res = await _objectsManager.List(orgId, dsId, path);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller GetInfo('{orgId}', '{dsId}', '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("obj/{**path}")]
        public async Task<IActionResult> Post([FromRoute] string orgId, [FromRoute] string dsId, string path, IFormFile file)
        {
            try
            {
                _logger.LogDebug($"Objects controller Post('{orgId}', '{dsId}', '{path}', '{file?.FileName}')");

                if (file == null)
                    return BadRequest(new ErrorResponse("No file uploaded"));

                await using var memory = new MemoryStream();
                await file.CopyToAsync(memory);

                var newObj = await _objectsManager.AddNew(orgId, dsId, path, memory.ToArray());
                return CreatedAtRoute(nameof(ObjectsController) + "." + nameof(GetInfo), new { path = newObj.Path },
                    newObj);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Post('{orgId}', '{dsId}', '{path}', '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        // DELETE: ddb/id
        [HttpDelete("obj/{**path}")]
        public async Task<IActionResult> Delete([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {

            try
            {
                _logger.LogDebug($"Objects controller Delete('{orgId}', '{dsId}', '{path}')");

                await _objectsManager.Delete(orgId, dsId, path);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Delete('{orgId}', '{dsId}', '{path}')");

                return ExceptionResult(ex);
            }

        }

        
    }
}
