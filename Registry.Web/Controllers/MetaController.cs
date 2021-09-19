using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [ApiController]
    [Route(RoutesHelper.OrganizationsRadix + "/" +
           RoutesHelper.OrganizationSlug + "/" +
           RoutesHelper.DatasetRadix + "/" +
           RoutesHelper.DatasetSlug + "/" +
           RoutesHelper.MetaRadix)]
    public class MetaController : ControllerBaseEx
    {
        private readonly IMetaManager _metaManager;
        private readonly ILogger<MetaController> _logger;

        public MetaController(IMetaManager metaManager, ILogger<MetaController> logger)
        {
            _metaManager = metaManager;
            _logger = logger;
        }

        // This is the correct approach
        [HttpPost("add/{key}", Name = nameof(MetaController) + "." + nameof(Add))]
        public async Task<IActionResult> Add([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string key, [FromBody] string data, [FromQuery] string path = null)
        {
            try
            {
                _logger.LogDebug($"Meta Controller Add('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Add(orgSlug, dsSlug, key, data, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller Add('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        // This is a monstrosity, but it works :)
        // basically key and path can be either query or formdata parameters
        [HttpPost("add", Name = nameof(MetaController) + "." + nameof(AddAlt))]
        public async Task<IActionResult> AddAlt([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm(Name = "key")] string keyFromForm, [FromForm] string data,
            [FromForm(Name = "path")] string pathFromForm = null,
            [FromQuery(Name = "path")] string pathFromQuery = null,
            [FromQuery(Name = "key")] string keyFromQuery = null)
        {
            // C# magics, precedence to form parameter
            var path = pathFromForm ?? pathFromQuery;
            var key = keyFromForm ?? keyFromQuery;

            try
            {
                _logger.LogDebug($"Meta Controller AddAlt('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Add(orgSlug, dsSlug, key, data, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller Add('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        [HttpPost("set/{key}", Name = nameof(MetaController) + "." + nameof(Set))]
        public async Task<IActionResult> Set([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string key, [FromBody] string data, [FromQuery] string path = null)
        {
            try
            {
                _logger.LogDebug($"Meta Controller Set('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Set(orgSlug, dsSlug, key, data, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller Set('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        [HttpPost("set", Name = nameof(MetaController) + "." + nameof(SetAlt))]
        public async Task<IActionResult> SetAlt([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm(Name = "key")] string keyFromForm, [FromForm] string data, [FromForm(Name = "path")] string pathFromForm = null,
            [FromQuery(Name = "path")] string pathFromQuery = null,
            [FromQuery(Name = "key")] string keyFromQuery = null)
        {
            // C# magics, precedence to form parameter
            var path = pathFromForm ?? pathFromQuery;
            var key = keyFromForm ?? keyFromQuery;

            try
            {
                _logger.LogDebug($"Meta Controller Set('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Set(orgSlug, dsSlug, key, data, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller Set('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        [HttpDelete("remove/{id}", Name = nameof(MetaController) + "." + nameof(Remove))]
        public async Task<IActionResult> Remove([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string id)
        {
            try
            {
                _logger.LogDebug($"Meta Controller Remove('{orgSlug}', '{dsSlug}', '{id}')");

                var res = await _metaManager.Remove(orgSlug, dsSlug, id);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller Remove('{orgSlug}', '{dsSlug}', '{id}')");
                return ExceptionResult(ex);
            }
        }

        [HttpPost("remove", Name = nameof(MetaController) + "." + nameof(RemoveAlt))]
        public async Task<IActionResult> RemoveAlt([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm] string id)
        {
            try
            {
                _logger.LogDebug($"Meta Controller RemoveAlt('{orgSlug}', '{dsSlug}', '{id}')");

                var res = await _metaManager.Remove(orgSlug, dsSlug, id);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller RemoveAlt('{orgSlug}', '{dsSlug}', '{id}')");
                return ExceptionResult(ex);
            }
        }

        [HttpDelete("unset/{key}", Name = nameof(MetaController) + "." + nameof(Unset))]
        public async Task<IActionResult> Unset([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string key, [FromQuery] string path = null)
        {
            try
            {
                _logger.LogDebug($"Meta Controller Unset('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Unset(orgSlug, dsSlug, key, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"Exception in Meta controller Remove('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        [HttpPost("unset", Name = nameof(MetaController) + "." + nameof(UnsetAlt))]
        public async Task<IActionResult> UnsetAlt([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromForm(Name = "key")] string keyFromForm, [FromForm(Name = "path")] string pathFromForm = null,
            [FromQuery(Name = "path")] string pathFromQuery = null,
            [FromQuery(Name = "key")] string keyFromQuery = null)
        {
            // C# magics, precedence to form parameter
            var path = pathFromForm ?? pathFromQuery;
            var key = keyFromForm ?? keyFromQuery;

            try
            {
                _logger.LogDebug($"Meta Controller UnsetAlt('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Unset(orgSlug, dsSlug, key, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"Exception in Meta controller UnsetAlt('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        [HttpGet("list", Name = nameof(MetaController) + "." + nameof(List))]
        public async Task<IActionResult> List([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromQuery] string path = null)
        {
            try
            {
                _logger.LogDebug($"Meta Controller List('{orgSlug}', '{dsSlug}', '{path}')");

                var res = await _metaManager.List(orgSlug, dsSlug, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller List('{orgSlug}', '{dsSlug}', '{path}')");
                return ExceptionResult(ex);
            }
        }

        [HttpGet("get/{key}", Name = nameof(MetaController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string key, [FromQuery] string path = null)
        {
            try
            {
                _logger.LogDebug($"Meta Controller Get('{orgSlug}', '{dsSlug}', '{key}', '{path}')");

                var res = await _metaManager.Get(orgSlug, dsSlug, key, path);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Meta controller Get('{orgSlug}', '{dsSlug}', '{key}', '{path}')");
                return ExceptionResult(ex);
            }
        }
    }
}