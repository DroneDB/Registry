using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Registry.Common;
using Registry.Web.Attributes;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route(RoutesHelper.ShareRadix)]
    public class ShareController : ControllerBaseEx
    {
        private readonly IShareManager _shareManager;

        private readonly ILogger<ShareController> _logger;

        public ShareController(IShareManager shareManager, ILogger<ShareController> logger)
        {
            _shareManager = shareManager;
            _logger = logger;
        }

        [HttpPost("init")]
        public async Task<IActionResult> Init([FromForm] ShareInitDto parameters)
        {
            try
            {
                _logger.LogDebug("Share controller Init('{OrgSlug}', '{DsSlug}', '{DatasetName}')", parameters.OrgSlug,
                    parameters.DsSlug, parameters?.DatasetName);

                var initRes = await _shareManager.Initialize(parameters);

                return Ok(initRes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Share controller Init('{OrgSlug}', '{DsSlug}', '{DatasetName}')", parameters.OrgSlug,
                    parameters.DsSlug, parameters?.DatasetName);

                return ExceptionResult(ex);
            }
        }

        [HttpGet("info/{token}")]
        public async Task<IActionResult> Info(string token)
        {
            try
            {
                _logger.LogDebug("Share controller Info('{Token}')", token);

                var res = await _shareManager.GetBatchInfo(token);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Share controller Info('{Token}')", token);

                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload/{token}")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<IActionResult> Upload(string token, [FromForm] string path, IFormFile file)
        {
            try
            {
                _logger.LogDebug("Share controller Upload('{Token}', '{Path}', '{file?.FileName}')", token, path,
                    file?.FileName);

                if (file == null)
                    throw new ArgumentException("No file uploaded");

                await using var stream = file.OpenReadStream();
                var res = await _shareManager.Upload(token, path, stream);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Share controller Upload('{Token}', '{Path}', '{file?.FileName}')",
                    token, path, file?.FileName);

                return ExceptionResult(ex);
            }
        }

        [HttpPost("commit/{token}")]
        public async Task<IActionResult> Commit(string token)
        {
            try
            {
                _logger.LogDebug("Share controller Commit('{Token}')", token);

                var res = await _shareManager.Commit(token);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Share controller Commit('{Token}')", token);

                return ExceptionResult(ex);
            }
        }

        [HttpPost("rollback/{token}")]
        public async Task<IActionResult> Rollback(string token)
        {
            try
            {
                _logger.LogDebug("Share controller Rollback('{Token}')", token);

                await _shareManager.Rollback(token);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Share controller Rollback('{Token}')", token);

                return ExceptionResult(ex);
            }
        }
    }
}