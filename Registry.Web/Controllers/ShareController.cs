using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
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
    private readonly IObjectsManager _objectsManager;

    private readonly ILogger<ShareController> _logger;
    private readonly FormOptions _formOptions;
    private readonly AppSettings _appSettings;

    public ShareController(IShareManager shareManager, IObjectsManager objectsManager,
        ILogger<ShareController> logger, IOptions<FormOptions> formOptions, IOptions<AppSettings> appSettings)
    {
        _shareManager = shareManager;
        _objectsManager = objectsManager;
        _logger = logger;
        _formOptions = formOptions.Value;
        _appSettings = appSettings.Value;
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

            throw;
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

            throw;
        }
    }

    /// <summary>
    /// Uploads a file to the share by streaming a <c>multipart/form-data</c> request: the single file
    /// part is written straight to the dataset volume and the destination is taken from a <c>path</c> form field.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <returns>The upload result.</returns>
    [HttpPost("upload/{token}", Name = nameof(ShareController) + "." + nameof(Upload))]
    [DisableFormValueModelBinding]
    [ConfigurableUploadSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ProducesResponseType(typeof(UploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Upload([FromRoute, Required] string token)
    {
        try
        {
            if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
                return BadRequest(new ErrorResponse("Expected a multipart/form-data request"));

            // Early reject oversized uploads (defence-in-depth; Kestrel also enforces the limit mid-stream)
            if (_appSettings.MaxRequestBodySize is { } maxBody &&
                Request.ContentLength is { } contentLength && contentLength > maxBody)
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new ErrorResponse($"Upload exceeds the maximum allowed size of {maxBody} bytes"));

            // Resolve the dataset target so the file can be streamed straight onto its storage volume
            var (orgSlug, dsSlug) = await _shareManager.GetUploadTarget(token);

            StreamedFile file = null;
            var form = await MultipartRequestHelper.ReadSingleFileFormAsync(Request,
                _formOptions.MultipartBoundaryLengthLimit,
                async body =>
                {
                    file = await _objectsManager.StreamToTempAsync(orgSlug, dsSlug, body);
                    return (file.TempPath, file.Bytes);
                });
            var path = form.GetField("path");

            if (file == null)
                return BadRequest(new ErrorResponse("No file uploaded"));

            _logger.LogDebug("Share controller Upload('{Token}', '{Path}')", token, path);

            var res = await _shareManager.UploadStreamed(token, path, file);
            return Ok(res);
        }
        catch (BadHttpRequestException bex) when (bex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new ErrorResponse("Upload exceeds the maximum allowed size"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Share controller Upload('{Token}')", token);

            throw;
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

            throw;
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

            throw;
        }
    }

    /// <summary>
    /// Uploads a chunk of a large file. The form fields (<c>fileId</c>, <c>chunkIndex</c>,
    /// <c>totalChunks</c>, <c>totalFileSize</c>, <c>chunkSize</c> when there is more than one chunk,
    /// <c>path</c>, <c>fileName</c>) come before the <c>chunk</c> file part, which is streamed
    /// straight into the file at its offset. When the last chunk is stored the file is added to the
    /// batch's dataset.
    /// </summary>
    /// <param name="token">The share token.</param>
    /// <returns>The chunk upload result, or the final upload result if this was the last chunk.</returns>
    [HttpPost("upload-chunk/{token}", Name = nameof(ShareController) + "." + nameof(UploadChunked))]
    [DisableFormValueModelBinding]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ConfigurableUploadSizeLimit]
    [ProducesResponseType(typeof(ChunkUploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadChunked([FromRoute, Required] string token)
    {
        if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
            return BadRequest(new ErrorResponse("Expected a multipart/form-data request"));

        ChunkUploadDto chunkInfo = null;
        ChunkUploadResultDto result = null;
        try
        {
            await MultipartRequestHelper.ReadFilesFormAsync(Request, _formOptions.MultipartBoundaryLengthLimit, 1,
                async part =>
                {
                    chunkInfo = ChunkFields.ToShareChunk(part.Fields);
                    _logger.LogDebug("Share controller UploadChunked('{Token}', ChunkIndex: {ChunkIndex}, TotalChunks: {TotalChunks}, FileId: {FileId})",
                        token, chunkInfo.ChunkIndex, chunkInfo.TotalChunks, chunkInfo.FileId);
                    result = await _shareManager.UploadChunk(token, chunkInfo, part.Body);
                    return result;
                }, _ => null);

            if (result == null || chunkInfo == null)
                throw new ArgumentException("No chunk uploaded");

            if (result.IsComplete)
            {
                _logger.LogInformation("All chunks received for file {FileName}, finalizing upload", chunkInfo.FileName);
                return Ok(await _shareManager.FinalizeChunkedUpload(token, chunkInfo.FileId, chunkInfo.Path));
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Share controller UploadChunked('{Token}', FileId: {FileId}, ChunkIndex: {ChunkIndex})",
                token, chunkInfo?.FileId, chunkInfo?.ChunkIndex);

            throw;
        }
    }
}