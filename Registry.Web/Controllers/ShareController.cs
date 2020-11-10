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
using System.Threading.Tasks;
using Registry.Common;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("share")]
    public class ShareController : ControllerBaseEx
    {
        private readonly IShareManager _shareManager;
        private readonly IChunkedUploadManager _chunkedUploadManager;

        private readonly ILogger<ShareController> _logger;

        public ShareController(IShareManager shareManager, ILogger<ShareController> logger, IChunkedUploadManager chunkedUploadManager)
        {
            _shareManager = shareManager;
            _logger = logger;
            _chunkedUploadManager = chunkedUploadManager;
        }

        [HttpPost("init")]
        public async Task<IActionResult> Init([FromForm] ShareInitDto parameters)
        {
            try
            {
                _logger.LogDebug($"Share controller Init('{parameters}')");

                var initRes = await _shareManager.Initialize(parameters);

                return Ok(initRes);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller Init('{parameters}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload/{token}")]
        public async Task<IActionResult> Upload(string token, [FromForm] string path, IFormFile file)
        {
            try
            {

                _logger.LogDebug($"Share controller Upload('{token}', '{path}', '{file?.FileName}')");

                if (file == null)
                    throw new ArgumentException("No file uploaded");

                await using var stream = file.OpenReadStream();
                var res = await _shareManager.Upload(token, path, stream);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller Upload('{token}', '{path}', '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload/{token}/session")]
        public async Task<IActionResult> NewUploadSession(string token, [FromForm] int chunks, [FromForm] long size)
        {
            try
            {

                _logger.LogDebug($"Share controller NewUploadSession('{token}', {chunks}, {size})");

                var sessionId = await _shareManager.StartUploadSession(token, chunks, size);

                return Ok(new UploadNewSessionResultDto
                {
                    SessionId = sessionId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller NewUploadSession('{token}', {chunks}, {size})");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload/{token}/session/{sessionId}/chunk/{index}")]
        public async Task<IActionResult> UploadToSession(string token, int sessionId, int index, IFormFile file)
        {
            try
            {

                _logger.LogDebug($"Share controller UploadToSession('{token}', {sessionId}, {index}, '{file?.FileName}')");

                if (file == null)
                    throw new ArgumentException("No file uploaded");

                await using var stream = file.OpenReadStream();

                await _shareManager.UploadToSession(token, sessionId, index, stream);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller UploadToSession('{token}', {sessionId}, {index}, '{file?.FileName}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload/{token}/session/{sessionId}/close")]
        public async Task<IActionResult> CloseSession(string token, int sessionId, [FromForm] string path)
        {
            try
            {

                _logger.LogDebug($"Share controller CloseSession('{token}', {sessionId}, '{path}')");

                var ret = await _shareManager.CloseUploadSession(token, sessionId, path);

                return Ok(ret);
                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller CloseSession('{token}', {sessionId}, '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("commit/{token}")]
        public async Task<IActionResult> Commit(string token)
        {
            try
            {

                _logger.LogDebug($"Share controller Commit('{token}')");

                var res = await _shareManager.Commit(token);

                return Ok(res);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Share controller Commit('{token}')");

                return ExceptionResult(ex);
            }
        }

    }
}
