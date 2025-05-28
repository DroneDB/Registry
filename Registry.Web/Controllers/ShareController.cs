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

namespace Registry.Web.Controllers;

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
            _logger.LogDebug("Share controller Init('{Tag}')", parameters.Tag);

            var initRes = await _shareManager.Initialize(parameters);

            return Ok(initRes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Share controller Init('{Tag}')", parameters.Tag);

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

    [HttpPost("upload-chunk/{token}")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadChunked(string token, [FromForm] ChunkUploadDto chunkInfo, IFormFile chunk)
    {
        try
        {
            _logger.LogDebug("Share controller UploadChunked('{Token}', ChunkIndex: {ChunkIndex}, TotalChunks: {TotalChunks}, FileId: {FileId})", 
                token, chunkInfo.ChunkIndex, chunkInfo.TotalChunks, chunkInfo.FileId);

            if (chunk == null)
                throw new ArgumentException("No chunk uploaded");

            if (string.IsNullOrEmpty(chunkInfo.FileId))
                throw new ArgumentException("FileId is required");

            await using var stream = chunk.OpenReadStream();
            
            // Use a memory-efficient stream processing approach
            var result = await _shareManager.UploadChunk(token, chunkInfo, stream);
            
            // Checking if this was the last chunk and all chunks are received
            if (result.IsComplete)
            {
                _logger.LogInformation("All chunks received for file {FileName}, finalizing upload", chunkInfo.FileName);
                var finalResult = await _shareManager.FinalizeChunkedUpload(token, chunkInfo.FileId, chunkInfo.Path);
                return Ok(finalResult);
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Share controller UploadChunked('{Token}', FileId: {FileId}, ChunkIndex: {ChunkIndex})",
                token, chunkInfo.FileId, chunkInfo.ChunkIndex);

            return ExceptionResult(ex);
        }
    }
}