using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using System;
using System.ComponentModel.DataAnnotations;
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

/// <summary>
/// Controller for managing file sharing and upload operations.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.ShareRadix)]
[Produces("application/json")]
public class ShareController : ControllerBaseEx
{
    private readonly IShareManager _shareManager;

    private readonly ILogger<ShareController> _logger;

    public ShareController(IShareManager shareManager, ILogger<ShareController> logger)
    {
        _shareManager = shareManager;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new share session for uploading files.
    /// </summary>
    /// <param name="parameters">The share initialization parameters.</param>
    /// <returns>The share initialization result containing the token.</returns>
    [HttpPost("init", Name = nameof(ShareController) + "." + nameof(Init))]
    [ProducesResponseType(typeof(ShareInitResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Init([FromForm, Required] ShareInitDto parameters)
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

    /// <summary>
    /// Gets information about a share batch.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <returns>The batch information.</returns>
    [HttpGet("info/{token}", Name = nameof(ShareController) + "." + nameof(Info))]
    [ProducesResponseType(typeof(BatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Info([FromRoute, Required] string token)
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

    /// <summary>
    /// Uploads a file to the share.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <param name="path">The destination path for the file.</param>
    /// <param name="file">The file to upload.</param>
    /// <returns>The upload result.</returns>
    [HttpPost("upload/{token}", Name = nameof(ShareController) + "." + nameof(Upload))]
    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ProducesResponseType(typeof(UploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Upload(
        [FromRoute, Required] string token,
        [FromForm, Required] string path,
        [Required] IFormFile file)
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

    /// <summary>
    /// Commits the share, finalizing all uploaded files.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <returns>The commit result containing the URL and tag information.</returns>
    [HttpPost("commit/{token}", Name = nameof(ShareController) + "." + nameof(Commit))]
    [ProducesResponseType(typeof(CommitResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Commit([FromRoute, Required] string token)
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

    /// <summary>
    /// Rolls back the share, canceling all uploaded files.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <returns>OK if the rollback was successful.</returns>
    [HttpPost("rollback/{token}", Name = nameof(ShareController) + "." + nameof(Rollback))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rollback([FromRoute, Required] string token)
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

    /// <summary>
    /// Uploads a chunk of a large file.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <param name="chunkInfo">Information about the chunk being uploaded.</param>
    /// <param name="chunk">The chunk data.</param>
    /// <returns>The chunk upload result, or the final upload result if this was the last chunk.</returns>
    [HttpPost("upload-chunk/{token}", Name = nameof(ShareController) + "." + nameof(UploadChunked))]
    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ProducesResponseType(typeof(ChunkUploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadChunked(
        [FromRoute, Required] string token,
        [FromForm, Required] ChunkUploadDto chunkInfo,
        [Required] IFormFile chunk)
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