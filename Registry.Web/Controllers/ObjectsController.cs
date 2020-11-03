using System;
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
    [Route("orgs/{orgSlug:regex([[\\w-]]+)}/ds/{dsSlug:regex([[\\w-]]+)}")]
    public class ObjectsController : ControllerBaseEx
    {
        private readonly IObjectsManager _objectsManager;
        private readonly ILogger<ObjectsController> _logger;

        public ObjectsController(IObjectsManager datasetsManager, ILogger<ObjectsController> logger)
        {
            _objectsManager = datasetsManager;
            _logger = logger;
        }
        
        [HttpGet("obj", Name = nameof(ObjectsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path)
        {
            try
            {
                _logger.LogDebug($"Objects controller Get('{orgSlug}', '{dsSlug}', '{path}')");

                var res = await _objectsManager.Get(orgSlug, dsSlug, path);
                return File(res.Data, res.ContentType, res.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Get('{orgSlug}', '{dsSlug}', '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpGet("info", Name = nameof(ObjectsController) + "." + nameof(GetInfo))]
        public async Task<IActionResult> GetInfo([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path)
        {
            try
            {
                _logger.LogDebug($"Objects controller GetInfo('{orgSlug}', '{dsSlug}', '{path}')");

                var res = await _objectsManager.List(orgSlug, dsSlug, path);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller GetInfo('{orgSlug}', '{dsSlug}', '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("obj")]
        public async Task<IActionResult> Post([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path, IFormFile file)
        {
            try
            {
                _logger.LogDebug($"Objects controller Post('{orgSlug}', '{dsSlug}', '{path}', '{file?.FileName}')");

                if (file == null)
                    return BadRequest(new ErrorResponse("No file uploaded"));

                await using var stream = file.OpenReadStream();

                var newObj = await _objectsManager.AddNew(orgSlug, dsSlug, path, stream);
                return CreatedAtRoute(nameof(ObjectsController) + "." + nameof(GetInfo), new
                    {
                        orgSlug = orgSlug,
                        dsSlug = dsSlug,
                        path = newObj.Path
                    },
                    newObj);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Post('{orgSlug}', '{dsSlug}', '{path}', '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        [HttpDelete("obj")]
        public async Task<IActionResult> Delete([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path)
        {

            try
            {
                _logger.LogDebug($"Objects controller Delete('{orgSlug}', '{dsSlug}', '{path}')");

                await _objectsManager.Delete(orgSlug, dsSlug, path);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Delete('{orgSlug}', '{dsSlug}', '{path}')");

                return ExceptionResult(ex);
            }

        }

        
    }
}
