#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Attributes;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Uploads into a dataset (UPL-01): single files, bundles of small files in one request, and the
/// state of an upload by its <c>X-Upload-Id</c>.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug)]
[Produces("application/json")]
public class ObjectUploadsController : ControllerBaseEx
{
    /// <summary>Client-generated id of an upload, echoed in its receipt.</summary>
    public const string UploadIdHeader = "X-Upload-Id";

    private readonly IUploadManager _uploads;
    private readonly IChunkedUploadManager _chunks;
    private readonly IObjectsManager _objectsManager;
    private readonly ILogger<ObjectUploadsController> _logger;
    private readonly FormOptions _formOptions;
    private readonly AppSettings _appSettings;
    private readonly UploadSettings _uploadSettings;

    public ObjectUploadsController(IUploadManager uploads, IChunkedUploadManager chunks, IObjectsManager objectsManager,
        ILogger<ObjectUploadsController> logger, IOptions<FormOptions> formOptions, IOptions<AppSettings> appSettings)
    {
        _uploads = uploads;
        _chunks = chunks;
        _objectsManager = objectsManager;
        _logger = logger;
        _formOptions = formOptions.Value;
        _appSettings = appSettings.Value;
        _uploadSettings = appSettings.Value.Uploads ?? new UploadSettings();
    }

    /// <summary>
    /// Creates a folder or uploads files to a dataset via a <c>multipart/form-data</c> request.
    /// Each file part is streamed straight to the dataset volume and hashed on the way; its
    /// destination is the <c>path</c> field sent before it (or, for a single file, anywhere in the
    /// form). Without a file part a folder is created at <c>path</c>. Access, quota (from
    /// <c>Content-Length</c>) and a <c>path</c> sent before its file are checked before the file's
    /// first byte is stored.
    /// </summary>
    /// <remarks>
    /// One file part: <c>201</c> with the entry. Several file parts (a bundle): <c>200</c> with one
    /// result per file; a file that fails does not fail the others. With an <c>X-Upload-Id</c>
    /// header the outcome can be asked for later (<c>GET obj/uploads/{uploadId}</c>), and a request
    /// repeating the id of an upload that is already done is answered from its receipt.
    /// </remarks>
    [HttpPost(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + ".Post")]
    [DisableFormValueModelBinding]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ConfigurableUploadSizeLimit]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(UploadPartResultDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status507InsufficientStorage)]
    public async Task<IActionResult> Post(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType ?? string.Empty))
            return BadRequest(new ErrorResponse("Expected a multipart/form-data request"));

        // Early reject oversized uploads (defence-in-depth; Kestrel also enforces the limit mid-stream)
        if (_appSettings.MaxRequestBodySize is { } maxBody &&
            Request.ContentLength is { } contentLength && contentLength > maxBody)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new ErrorResponse($"Upload exceeds the maximum allowed size of {maxBody} bytes"));

        var uploadId = Request.Headers[UploadIdHeader].FirstOrDefault();

        try
        {
            using var ctx = await _uploads.BeginAsync(orgSlug, dsSlug, Request.ContentLength, uploadId);

            // The same upload already went through (its response was lost): answer from its receipt
            if (ctx.Receipt?.Results is { Count: > 0 } done)
                return Respond(orgSlug, dsSlug, done, done.Count > 1);

            StreamedMultipartFiles<StreamedFile> form;
            try
            {
                form = await MultipartRequestHelper.ReadFilesFormAsync(Request,
                    _formOptions.MultipartBoundaryLengthLimit, _uploadSettings.MaxPartsPerRequest,
                    async part =>
                    {
                        // Refuse a destination before its first byte is written
                        if (part.Path != null) _uploads.ValidatePath(ctx, part.Path);
                        return await _uploads.StreamAsync(ctx, part.Body, HttpContext.RequestAborted);
                    }, file => file.TempPath);
            }
            catch (Exception ex)
            {
                await _uploads.FailAsync(ctx, ex);
                throw;
            }

            _logger.LogDebug("Upload to '{OrgSlug}/{DsSlug}': {Count} file(s)", orgSlug, dsSlug, form.Files.Count);

            if (form.Files.Count == 0)
            {
                // No file part: folder creation (path provided as a form field)
                var folder = await _objectsManager.AddNew(orgSlug, dsSlug, form.GetField("path"));
                return Created(orgSlug, dsSlug, folder);
            }

            if (form.Files.Count == 1)
            {
                var (path, file) = form.Files[0];
                var entry = await _uploads.CommitAsync(ctx, path ?? form.GetField("path") ?? string.Empty, file);
                return Created(orgSlug, dsSlug, entry);
            }

            var parts = form.Files.Select(f => new StreamedPart(f.Path ?? string.Empty, f.File)).ToArray();
            return Respond(orgSlug, dsSlug, await _uploads.CommitAsync(ctx, parts), bundle: true);
        }
        catch (BadHttpRequestException bex) when (bex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new ErrorResponse("Upload exceeds the maximum allowed size"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in upload to '{OrgSlug}/{DsSlug}'", orgSlug, dsSlug);
            throw;
        }
    }

    /// <summary>
    /// The state of an upload by the <c>X-Upload-Id</c> it was sent with. A client whose request
    /// ended without a response (e.g. a proxy timeout after the last byte) asks here before sending
    /// the file again.
    /// </summary>
    /// <returns>200 with the receipt when the upload is done or failed; 202 while it is still being
    /// received or added; 404 when the id is unknown (send the file again).</returns>
    [HttpGet(RoutesHelper.ObjectsRadix + "/uploads/{uploadId}", Name = nameof(ObjectUploadsController) + "." + nameof(GetUpload))]
    [ProducesResponseType(typeof(UploadReceipt), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UploadReceipt), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUpload([FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, [FromRoute, Required] string uploadId)
    {
        var receipt = await _uploads.GetReceiptAsync(orgSlug, dsSlug, uploadId);
        Response.Headers.CacheControl = "no-store";

        return receipt switch
        {
            null => NotFound(new ErrorResponse($"Unknown upload '{uploadId}'")),
            { State: UploadReceiptState.Receiving or UploadReceiptState.Committing } => Accepted(receipt),
            _ => Ok(receipt)
        };
    }

    /// <summary>
    /// Stores one chunk of a resumable upload (files larger than a single request should carry).
    /// The form fields <c>fileId</c>, <c>chunkIndex</c>, <c>totalChunks</c>, <c>totalFileSize</c>,
    /// <c>chunkSize</c> and <c>path</c> come before the <c>chunk</c> file part, which is streamed
    /// straight into the file at its offset. Chunks can be sent in any order and again; the first
    /// one checks access, path and quota. Commit with <c>POST obj/chunk/{fileId}/commit</c>.
    /// </summary>
    [HttpPost(RoutesHelper.ObjectsRadix + "/chunk", Name = nameof(ObjectUploadsController) + "." + nameof(PostChunk))]
    [DisableFormValueModelBinding]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ConfigurableUploadSizeLimit]
    [ProducesResponseType(typeof(ChunkStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status507InsufficientStorage)]
    public async Task<IActionResult> PostChunk([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug)
    {
        if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType ?? string.Empty))
            return BadRequest(new ErrorResponse("Expected a multipart/form-data request"));

        if (Request.ContentLength is { } length && length > _uploadSettings.MaxChunkBytes + 64 * 1024)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new ErrorResponse($"A chunk can be at most {_uploadSettings.MaxChunkBytes} bytes"));

        ChunkStatusDto? status = null;
        await MultipartRequestHelper.ReadFilesFormAsync(Request, _formOptions.MultipartBoundaryLengthLimit, 1,
            async part =>
            {
                status = await _chunks.WriteChunkAsync(orgSlug, dsSlug, ChunkFields.ToDatasetChunk(part.Fields), part.Body,
                    HttpContext.RequestAborted);
                return status;
            }, _ => null!);

        return status == null ? BadRequest(new ErrorResponse("No chunk uploaded")) : Ok(status);
    }

    /// <summary>Which chunks of a resumable upload are stored and which are missing.</summary>
    [HttpGet(RoutesHelper.ObjectsRadix + "/chunk/{fileId}", Name = nameof(ObjectUploadsController) + "." + nameof(GetChunks))]
    [ProducesResponseType(typeof(ChunkStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChunks([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string fileId)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _chunks.GetStatusAsync(orgSlug, dsSlug, fileId));
    }

    /// <summary>
    /// Adds a resumable upload whose chunks are all stored to the dataset. Idempotent: committing a
    /// committed upload again returns its entry. 409 lists the missing chunks.
    /// </summary>
    [HttpPost(RoutesHelper.ObjectsRadix + "/chunk/{fileId}/commit", Name = nameof(ObjectUploadsController) + "." + nameof(CommitChunks))]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CommitChunks([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string fileId) =>
        Created(orgSlug, dsSlug, await _chunks.CommitAsync(orgSlug, dsSlug, fileId));

    private IActionResult Respond(string orgSlug, string dsSlug, IReadOnlyList<UploadPartResultDto> results, bool bundle)
    {
        if (bundle)
            return Ok(results);

        var single = results[0];
        return single.Entry != null
            ? Created(orgSlug, dsSlug, single.Entry)
            : StatusCode(single.Status, new ErrorResponse(single.Error ?? "Upload failed"));
    }

    private IActionResult Created(string orgSlug, string dsSlug, EntryDto entry) =>
        CreatedAtRoute(nameof(ObjectsController) + "." + nameof(ObjectsController.GetInfo),
            new { orgSlug, dsSlug, path = entry.Path }, entry);
}
