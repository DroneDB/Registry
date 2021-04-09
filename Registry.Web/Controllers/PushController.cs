using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
           RoutesHelper.PushRadix)]
    public class PushController : ControllerBaseEx
    {
        private readonly IPushManager _pushManager;
        private readonly ILogger<PushController> _logger;

        public PushController(IPushManager pushManager, ILogger<PushController> logger)
        {
            _pushManager = pushManager;
            _logger = logger;
        }

        [HttpPost("init")]
        public async Task<IActionResult> Init([FromRoute] string orgSlug, [FromRoute] string dsSlug, IFormFile file)
        {
            try
            {
                _logger.LogDebug($"Push controller Init('{orgSlug}', '{dsSlug}', '{file?.FileName}')");

                if (file == null)
                    throw new ArgumentException("No file uploaded");

                await using var stream = file.OpenReadStream();
                
                var res = await _pushManager.Init(orgSlug, dsSlug, stream);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Push controller Init('{orgSlug}', '{dsSlug}', '{file?.FileName}')");

                return ExceptionResult(ex);
            }


        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromRoute] string orgSlug, [FromRoute] string dsSlug, string path, IFormFile file)
        {
            try
            {
                _logger.LogDebug($"Push controller Upload('{orgSlug}', '{dsSlug}', '{file?.FileName}')");

                if (file == null)
                    throw new ArgumentException("No file uploaded");

                await using var stream = file.OpenReadStream();
                await _pushManager.Upload(orgSlug, dsSlug, path, stream);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Push controller Upload('{orgSlug}', '{dsSlug}', '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("commit")]
        public async Task<IActionResult> Commit([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        {
            try
            {
                _logger.LogDebug($"Push controller Commit('{orgSlug}', '{dsSlug}')");

                await _pushManager.Commit(orgSlug, dsSlug);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Push controller Commit('{orgSlug}', '{dsSlug}')");

                return ExceptionResult(ex);
            }
        }


    }
}
