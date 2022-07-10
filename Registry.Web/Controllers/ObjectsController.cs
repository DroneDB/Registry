using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.CompilerServices;
using MimeMapping;
using Registry.Common;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Filters;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [ApiController]
    [Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
           "/" + RoutesHelper.DatasetSlug)]
    public class ObjectsController : ControllerBaseEx
    {
        private readonly IObjectsManager _objectsManager;
        private readonly ILogger<ObjectsController> _logger;

        public ObjectsController(IObjectsManager datasetsManager, ILogger<ObjectsController> logger)
        {
            _objectsManager = datasetsManager;
            _logger = logger;
        }

        [HttpGet("ddb", Name = nameof(ObjectsController) + "." + nameof(GetDdb))]
        public async Task<IActionResult> GetDdb([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        {
            try
            {
                _logger.LogDebug("Objects controller GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug, dsSlug);

                var res = await _objectsManager.GetDdb(orgSlug, dsSlug);

                Response.StatusCode = 200;
                Response.ContentType = res.ContentType;

                Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{res.Name}\"");

                await res.CopyToAsync(Response.Body);

                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Objects controller GetDdb('{OrgSlug}', '{DsSlug}')", orgSlug,
                    dsSlug);
                return ExceptionResult(ex);
            }
        }

        [HttpGet("thumb", Name = nameof(ObjectsController) + "." + nameof(GenerateThumbnail))]
        public async Task<IActionResult> GenerateThumbnail([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromQuery] string path, [FromQuery] int? size)
        {
            try
            {
                _logger.LogDebug("Objects controller GenerateThumbnail('{OrgSlug}', '{DsSlug}', '{Path}', '{Size}')",
                    orgSlug, dsSlug, path, size);

                var res = await _objectsManager.GenerateThumbnail(orgSlug, dsSlug, path, size);

                return PhysicalFile(res.PhysicalPath, res.ContentType, res.Name, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller GenerateThumbnail('{OrgSlug}', '{DsSlug}', '{Path}', '{Size}')",
                    orgSlug, dsSlug, path, size);
                return ExceptionResult(new Exception("Cannot generate thumbnail"));
            }
        }

        [HttpGet("tiles/{tz}/{tx}/{tyRaw}.png", Name = nameof(ObjectsController) + "." + nameof(GenerateTile))]
        public async Task<IActionResult> GenerateTile([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] int tz, [FromRoute] int tx, [FromRoute] string tyRaw, [FromQuery] string path)
        {
            try
            {
                _logger.LogDebug(
                    "Objects controller GenerateTile('{OrgSlug}', '{DsSlug}', '{Path}', '{Tz}', '{Tx}', '{TyRaw}')",
                    orgSlug, dsSlug, path, tz, tx, tyRaw);

                var retina = tyRaw.EndsWith("@2x");

                if (!int.TryParse(retina ? tyRaw.Replace("@2x", string.Empty) : tyRaw, out var ty))
                    throw new ArgumentException("Invalid input parameters (retina indicator should be '@2x')");

                var res = await _objectsManager.GenerateTile(orgSlug, dsSlug, path, tz, tx, ty, retina);

                return PhysicalFile(res.PhysicalPath, res.ContentType, res.Name, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller GenerateTile('{OrgSlug}', '{DsSlug}', '{Path}', '{Tz}', '{Tx}', '{TyRaw}')",
                    orgSlug, dsSlug, path, tz, tx, tyRaw);
                return ExceptionResult(ex);
            }
        }

        #region Downloads

        private async Task<IActionResult> InternalDownload(string orgSlug, string dsSlug, string[] paths, bool isInline)
        {
            try
            {
                // If only one file is requested, we can leverage the local file system
                if (paths?.Length == 1 &&
                    await _objectsManager.GetEntryType(orgSlug, dsSlug, paths[0]) != EntryType.Directory)
                {
                    var r = await _objectsManager.Get(orgSlug, dsSlug, paths[0]);
                    return PhysicalFile(r.PhysicalPath, r.ContentType, isInline ? null : r.Name, true);
                }

                var res = await _objectsManager.DownloadStream(orgSlug, dsSlug, paths);

                Response.StatusCode = 200;
                Response.ContentType = res.ContentType;

                Response.Headers.Add("Content-Disposition",
                    isInline ? "inline" : $"attachment; filename=\"{res.Name}\"");

                await res.CopyToAsync(Response.Body);

                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller Download('{OrgSlug}', '{DsSlug}', '{Paths}', '{IsInline}')",
                    orgSlug, dsSlug, paths != null ? string.Join("; ", paths) : string.Empty, isInline);

                return ExceptionResult(ex);
            }
        }

        [HttpGet("download", Name = nameof(ObjectsController) + "." + nameof(Download))]
        public Task<IActionResult> Download([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromQuery(Name = "path")] string pathsRaw, [FromQuery(Name = "inline")] int? isInlineRaw)
        {
            var paths = pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);
            var isInline = isInlineRaw == 1;

            _logger.LogDebug("Objects controller Download('{OrgSlug}', '{DsSlug}', '{PathsRaw}', '{IsInlineRaw}')",
                orgSlug, dsSlug, pathsRaw, isInlineRaw);

            return InternalDownload(orgSlug, dsSlug, paths, isInline);
        }

        [HttpPost("download", Name = nameof(ObjectsController) + "." + nameof(DownloadPost))]
        public Task<IActionResult> DownloadPost([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm(Name = "path")] string pathsRaw, [FromForm(Name = "inline")] int? isInlineRaw)
        {
            var paths = pathsRaw?.Split(",", StringSplitOptions.RemoveEmptyEntries);
            var isInline = isInlineRaw == 1;

            _logger.LogDebug(
                "Objects controller DownloadPost('{OrgSlug}', '{DsSlug}', '{PathsRaw}', '{IsInlineRaw}')", orgSlug,
                dsSlug, pathsRaw, isInlineRaw);

            return InternalDownload(orgSlug, dsSlug, paths, isInline);
        }

        [HttpGet("download/{*path}", Name = nameof(ObjectsController) + "." + nameof(DownloadExact))]
        public async Task<IActionResult> DownloadExact([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            string path, [FromQuery(Name = "inline")] int? isInlineRaw)
        {
            var isInline = isInlineRaw == 1;

            _logger.LogDebug("Objects controller DownloadExact('{OrgSlug}', '{DsSlug}', '{Path}', '{IsInlineRaw}')",
                orgSlug, dsSlug, path, isInlineRaw);

            return await InternalDownload(orgSlug, dsSlug, new[] { path }, isInline);
        }


        #endregion

        [HttpGet(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string path)
        {
            try
            {
                _logger.LogDebug("Objects controller Get('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);

                var res = await _objectsManager.Get(orgSlug, dsSlug, path);
                return PhysicalFile(res.PhysicalPath, res.ContentType, res.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Objects controller Get('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug,
                    dsSlug, path);

                return ExceptionResult(ex);
            }
        }

        [HttpGet("list", Name = nameof(ObjectsController) + "." + nameof(GetInfo))]
        [ProducesResponseType(typeof(IEnumerable<Entry>), 200)]
        public async Task<IActionResult> GetInfo([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromQuery] string path, [FromQuery] EntryType? type = null)
        {
            try
            {
                _logger.LogDebug("Objects controller GetInfo('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                    dsSlug, path, type);

                var res = await _objectsManager.List(orgSlug, dsSlug, path, false, type);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller GetInfo('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                    dsSlug, path, type);

                return ExceptionResult(ex);
            }
        }

        [HttpPost("list", Name = nameof(ObjectsController) + "." + nameof(GetInfoEx))]
        [ProducesResponseType(typeof(IEnumerable<Entry>), 200)]
        public async Task<IActionResult> GetInfoEx([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string path, [FromForm] EntryType? type = null)
        {
            try
            {
                _logger.LogDebug("Objects controller GetInfoEx('{OrgSlug}', '{DsSlug}', '{Path}', {Type})", orgSlug,
                    dsSlug, path, type);

                var res = await _objectsManager.List(orgSlug, dsSlug, path, false, type);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller GetInfoEx('{OrgSlug}', '{DsSlug}', '{Path}, {Type})", orgSlug,
                    dsSlug, path, type);
                return ExceptionResult(ex);
            }
        }


        [HttpPost("search", Name = nameof(ObjectsController) + "." + nameof(Search))]
        [ProducesResponseType(typeof(IEnumerable<Entry>), 200)]
        public async Task<IActionResult> Search([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string query, [FromForm] string path, [FromForm] bool recursive = true,
            [FromForm] EntryType? type = null)
        {
            try
            {
                _logger.LogDebug("Objects controller Search('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                    dsSlug, path, type);

                var res = await _objectsManager.Search(orgSlug, dsSlug, query, path, recursive, type);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller Search('{OrgSlug}', '{DsSlug}', '{Path}', '{Type}')", orgSlug,
                    dsSlug, path, type);
                return ExceptionResult(ex);
            }
        }

        [HttpPost(RoutesHelper.ObjectsRadix)]
        [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> Post([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string path, IFormFile file = null)
        {
            try
            {
                _logger.LogDebug("Objects controller Post('{OrgSlug}', '{DsSlug}', '{Path}', '{file?.FileName}')",
                    orgSlug, dsSlug, path, file?.FileName);

                EntryDto newObj;

                if (file == null)
                {
                    newObj = await _objectsManager.AddNew(orgSlug, dsSlug, path);
                }
                else
                {
                    await using var stream = file.OpenReadStream();
                    newObj = await _objectsManager.AddNew(orgSlug, dsSlug, path, stream);
                }

                return CreatedAtRoute(nameof(ObjectsController) + "." + nameof(GetInfo), new
                {
                    orgSlug,
                    dsSlug,
                    path = newObj.Path
                }, newObj);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller Post('{OrgSlug}', '{DsSlug}', '{Path}', '{file?.FileName}')",
                    orgSlug, dsSlug, path, file?.FileName);

                return ExceptionResult(ex);
            }
        }


        [HttpDelete(RoutesHelper.ObjectsRadix)]
        public async Task<IActionResult> Delete([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string path)
        {
            try
            {
                _logger.LogDebug("Objects controller Delete('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);

                await _objectsManager.Delete(orgSlug, dsSlug, path);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Objects controller Delete('{OrgSlug}', '{DsSlug}', '{Path}')",
                    orgSlug, dsSlug, path);

                return ExceptionResult(ex);
            }
        }

        [HttpPut(RoutesHelper.ObjectsRadix)]
        public async Task<IActionResult> Move([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string source, [FromForm] string dest)
        {
            try
            {
                _logger.LogDebug("Objects controller Move('{OrgSlug}', '{DsSlug}', '{Source}', '{Dest}')", orgSlug,
                    dsSlug, source, dest);

                await _objectsManager.Move(orgSlug, dsSlug, source, dest);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller Move('{OrgSlug}', '{DsSlug}', '{Source}', '{Dest}')", orgSlug,
                    dsSlug, source, dest);

                return ExceptionResult(ex);
            }
        }

        [HttpPost("build", Name = nameof(ObjectsController) + "." + nameof(Build))]
        public async Task<IActionResult> Build([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string path, [FromForm] bool background = false, [FromForm] bool force = false)
        {
            try
            {
                _logger.LogDebug("Objects controller Build('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug, path);

                await _objectsManager.Build(orgSlug, dsSlug, path, background, force);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Objects controller Build('{OrgSlug}', '{DsSlug}', '{Path}')",
                    orgSlug, dsSlug, path);

                return ExceptionResult(ex);
            }
        }

        [ServiceFilter(typeof(BasicAuthFilter))]
        [HttpGet("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(BuildFile))]
        public async Task<IActionResult> BuildFile([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string hash, [FromRoute] string path)
        {
            try
            {
                _logger.LogDebug("Objects controller BuildFile('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug, dsSlug,
                    path);

                var res = await _objectsManager.GetBuildFile(orgSlug, dsSlug, hash, path);

                return PhysicalFile(res, MimeUtility.GetMimeMapping(res), true);
            }
            catch (UnauthorizedException ex)
            {
                BasicAuthFilter.SendBasicAuthRequest(Response);
                return ExceptionResult(ex);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        [ServiceFilter(typeof(BasicAuthFilter))]
        [HttpHead("build/{hash}/{*path}", Name = nameof(ObjectsController) + "." + nameof(BuildFile))]
        public async Task<IActionResult> CheckBuildFile([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string hash, [FromRoute] string path)
        {
            try
            {
                _logger.LogDebug("Objects controller CheckBuildFile('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug,
                    dsSlug, path);

                var res = await _objectsManager.CheckBuildFile(orgSlug, dsSlug, hash, path);

                return res ? Ok() : NotFound();
            }
            catch (UnauthorizedException ex)
            {
                BasicAuthFilter.SendBasicAuthRequest(Response);
                return ExceptionResult(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception in Objects controller CheckBuildFile('{OrgSlug}', '{DsSlug}', '{Path}')", orgSlug,
                    dsSlug, path);

                return ExceptionResult(ex);
            }
        }
    }
}