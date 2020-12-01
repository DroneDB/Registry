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
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{

    [Authorize]
    [ApiController]
    [Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
    public class ObjectsController : ControllerBaseEx
    {
        private readonly IObjectsManager _objectsManager;
        private readonly ILogger<ObjectsController> _logger;

        public ObjectsController(IObjectsManager datasetsManager, ILogger<ObjectsController> logger)
        {
            _objectsManager = datasetsManager;
            _logger = logger;
        }


        [HttpGet("thumb", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnail))]
        public async Task<IActionResult> GenerateThumbnail([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path, [FromForm] int? size)
        {
            try
            {
                _logger.LogDebug($"Objects controller GenerateThumbnail('{orgSlug}', '{dsSlug}', '{path}', '{size}')");

                var res = await _objectsManager.GenerateThumbnail(orgSlug, dsSlug, path, size);

                return File(res.ContentStream, res.ContentType, res.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller GenerateThumbnail('{orgSlug}', '{dsSlug}', '{path}', '{size}')");
                return ExceptionResult(ex);
            }
        }

        #region Downloads

        [HttpGet("download", Name = nameof(ObjectsController) + "." + nameof(Download))]
        public async Task<IActionResult> Download([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromQuery(Name = "path")] string pathsRaw)
        {
            try
            {

                var paths = pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);

                _logger.LogDebug($"Objects controller Download('{orgSlug}', '{dsSlug}', '{pathsRaw}')");

                var res = await _objectsManager.Download(orgSlug, dsSlug, paths);

                return File(res.ContentStream, res.ContentType, res.Name);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller Download('{orgSlug}', '{dsSlug}', '{pathsRaw}')");

                return ExceptionResult(ex);
            }
        }

        [AllowAnonymous]
        [HttpGet("package/{id}", Name = nameof(ObjectsController) + "." + nameof(DownloadPackage))]
        public async Task<IActionResult> DownloadPackage([FromRoute] string orgSlug, [FromRoute] string dsSlug, string id)
        {
            try
            {
                _logger.LogDebug($"Objects controller DownloadPackage('{orgSlug}', '{dsSlug}', '{id}')");

                var res = await _objectsManager.Download(orgSlug, dsSlug, id);

                return File(res.ContentStream, res.ContentType, res.Name);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller DownloadPackage('{orgSlug}', '{dsSlug}', '{id}')");

                return ExceptionResult(ex);
            }

        }

        [HttpPost("download", Name = nameof(ObjectsController) + "." + nameof(Download))]
        public async Task<IActionResult> GetPackageUrl([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm(Name = "path")] string[] paths, [FromForm] DateTime? expiration, [FromForm] bool isPublic)
        {
            var pathsJoined = paths != null ? string.Join(',', paths) : null;

            try
            {
                _logger.LogDebug(
                    $"Objects controller Download('{orgSlug}', '{dsSlug}', '{pathsJoined}', '{expiration}')");

                var res = await _objectsManager.GetDownloadPackage(orgSlug, dsSlug, paths, expiration, isPublic);

                var downloadUrl = Url.Link(nameof(ObjectsController) + "." + nameof(DownloadPackage), new
                {
                    orgSlug,
                    dsSlug,
                    id = res
                });

                return Ok(new DownloadPackageDto
                {
                    DownloadUrl = downloadUrl,
                    Expiration = expiration
                });

            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"Exception in Objects controller Download('{orgSlug}', '{dsSlug}', '{pathsJoined}')");

                return ExceptionResult(ex);
            }
        }
        #endregion

        [HttpGet(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + "." + nameof(Get))]
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

        [HttpPost("list", Name = nameof(ObjectsController) + "." + nameof(GetInfo))]
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

        [HttpPost(RoutesHelper.ObjectsRadix)]
        public async Task<IActionResult> Post([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path, IFormFile file)
        {
            try
            {
                _logger.LogDebug($"Objects controller Post('{orgSlug}', '{dsSlug}', '{path}', '{file?.FileName}')");

                if (file == null)
                    throw new ArgumentException("No file uploaded");

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


        [HttpDelete(RoutesHelper.ObjectsRadix)]
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

        #region Sessions

        [HttpPost(RoutesHelper.ObjectsRadix + "/session")]
        public async Task<IActionResult> PostNewSession([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] int chunks, [FromForm] long size)
        {
            try
            {

                _logger.LogDebug($"Objects controller PostNewSession('{orgSlug}', '{dsSlug}', {chunks}, {size})");

                var sessionId = await _objectsManager.AddNewSession(orgSlug, dsSlug, chunks, size);

                return Ok(new UploadNewSessionResultDto
                {
                    SessionId = sessionId
                });

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller PostNewSession('{orgSlug}', '{dsSlug}', {chunks}, {size})");

                return ExceptionResult(ex);
            }
        }

        [HttpPost(RoutesHelper.ObjectsRadix + "/session/{sessionId}/chunk/{index}")]
        public async Task<IActionResult> UploadToSession([FromRoute] string orgSlug, [FromRoute] string dsSlug, int sessionId, int index, IFormFile file)
        {
            try
            {

                _logger.LogDebug($"Objects controller UploadToSession('{orgSlug}', '{dsSlug}', {sessionId}, {index}, '{file?.FileName}')");

                if (file == null)
                    throw new ArgumentException("No file uploaded");

                await using var stream = file.OpenReadStream();

                await _objectsManager.AddToSession(orgSlug, dsSlug, sessionId, index, stream);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Objects controller UploadToSession('{orgSlug}', '{dsSlug}', {sessionId}, {index}, '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost(RoutesHelper.ObjectsRadix + "/session/{sessionId}/close")]
        public async Task<IActionResult> CloseSession([FromRoute] string orgSlug, [FromRoute] string dsSlug, int sessionId, [FromForm] string path)
        {
            try
            {

                _logger.LogDebug($"Objects controller CloseSession('{orgSlug}', '{dsSlug}', {sessionId}, '{path}')");

                var newObj = await _objectsManager.CloseSession(orgSlug, dsSlug, sessionId, path);

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
                _logger.LogError(ex, $"Exception in Objects controller CloseSession('{orgSlug}', '{dsSlug}', {sessionId}, '{path}')");

                return ExceptionResult(ex);
            }
        }

        #endregion


    }
}
