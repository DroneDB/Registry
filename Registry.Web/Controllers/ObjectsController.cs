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

        public ObjectsController(IObjectsManager datasetsManager)
        {
            _objectsManager = datasetsManager;
        }
        
        [HttpGet("obj/{**path}", Name = nameof(ObjectsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {
            try
            {
                var res = await _objectsManager.Get(orgId, dsId, path);
                return File(res.Data, res.ContentType, res.Name);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        [HttpGet("info/{**path}", Name = nameof(ObjectsController) + "." + nameof(GetInfo))]
        public async Task<IActionResult> GetInfo([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {
            try
            {

                var res = await _objectsManager.List(orgId, dsId, path);
                return Ok(res);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        /*
         *[HttpPost]
        public async Task<IActionResult> Upload(IFormFile uploadedFile)
        {
            if (uploadedFile == null || uploadedFile.Length == 0)
                return Content("file not selected");

            var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", uploadedFile.FileName);

            using (var stream = new FileStream(path, FileMode.Create))
            {
                await uploadedFile.CopyToAsync(stream);
            }

            return Ok();
        }
         *
         */

        [HttpPost("obj/{**path}")]
        public async Task<IActionResult> Post([FromRoute] string orgId, [FromRoute] string dsId, string path, IFormFile file)
        {
            try
            {
                await using var memory = new MemoryStream();
                await file.CopyToAsync(memory);

                var newObj = await _objectsManager.AddNew(orgId, dsId, path, memory.ToArray());
                return CreatedAtRoute(nameof(ObjectsController) + "." + nameof(GetInfo), new { path = newObj.Path },
                    newObj);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        // DELETE: ddb/id
        [HttpDelete("obj/{**path}")]
        public async Task<IActionResult> Delete([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {

            try
            {
                await _objectsManager.Delete(orgId, dsId, path);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        
    }
}
