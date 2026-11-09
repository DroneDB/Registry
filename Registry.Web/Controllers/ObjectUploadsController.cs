using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Forms;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Uploads into a dataset: single files, bundles of small files in one request, and the
/// state of an upload by its <c>X-Upload-Id</c>.
/// </summary>
[ApiController]
[Route(RoutesHelper.DatasetPrefix)]
[Produces("application/json")]
public class ObjectUploadsController : ControllerBaseEx
{
    /// <summary>Client-generated id of an upload, echoed in its receipt.</summary>
    public const string UploadIdHeader = "X-Upload-Id";

    private readonly UploadManager _uploads;
    private readonly ChunkedUploadManager _chunks;
    private readonly IEntryCommandService _commands;
    private readonly ILogger<ObjectUploadsController> _logger;
    private readonly FormOptions _formOptions;
    private readonly UploadSettings _uploadSettings;
    private readonly ApiErrorResponder _errors;

    public ObjectUploadsController(UploadManager uploads, ChunkedUploadManager chunks, IEntryCommandService commands,
        ILogger<ObjectUploadsController> logger, IOptions<FormOptions> formOptions, IOptions<UploadSettings> uploadSettings,
        ApiErrorResponder errors)
    {
        _uploads = uploads;
        _chunks = chunks;
        _commands = commands;
        _logger = logger;
        _formOptions = formOptions.Value;
        _uploadSettings = uploadSettings.Value;
        _errors = errors;
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
    /// A file whose name exists in its folder (without regard to letter case) is refused with <c>409</c>, the
    /// "already exists" message and <c>conflictPath</c>, unless the form field <c>overwrite=true</c> comes
    /// before its file part; then it replaces that entry, in the entry's letter case. The name is checked before
    /// the first byte is stored and again at the commit.
    /// One file part: <c>201</c> with the entry. Several file parts (a bundle): <c>200</c> with one
    /// result per file; a file that fails does not fail the others. With an <c>X-Upload-Id</c>
    /// header the outcome can be asked for later (<c>GET obj/uploads/{uploadId}</c>), and a request
    /// repeating the id of an upload that is already done is answered from its receipt.
    /// </remarks>
    [HttpPost(RoutesHelper.ObjectsRadix, Name = nameof(ObjectsController) + ".Post")]
    [TypeFilter(typeof(UploadAnswerFilter))]
    [DisableFormValueModelBinding]
    [StreamedRequestBody(typeof(ObjectUploadForm))]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ConfigurableUploadSizeLimit]
    [EnforceContentLength]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(UploadPartResultDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status507InsufficientStorage)]
    public async Task<IActionResult> Post(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType ?? string.Empty))
            throw new BadRequestException("Expected a multipart/form-data request");

        var uploadId = Request.Headers[UploadIdHeader].FirstOrDefault();

        using var ctx = await _uploads.BeginAsync(orgSlug, dsSlug, Request.ContentLength, uploadId, ct: ct);

        // The same upload already went through (its response was lost): answer from its receipt
        if (ctx.Receipt?.Results is { Count: > 0 } done)
            return Respond(orgSlug, dsSlug, done, done.Count > 1);

        StreamedMultipartFiles<ReceivedPart> form;
        try
        {
            form = await MultipartRequestHelper.ReadFilesFormAsync(Request,
                _formOptions.MultipartBoundaryLengthLimit, _uploadSettings.MaxPartsPerRequest,
                async part =>
                {
                    // The overwrite field sent before this file part, after the previous one, is the part's (D-040)
                    var overwrite = ChunkFields.Overwrite(part.Overwrite);
                    if (part.Path != null)
                    {
                        // Refuse a destination before its first byte is written
                        _uploads.ValidatePath(part.Path);
                        try
                        {
                            _uploads.ResolveTarget(ctx, part.Path, overwrite);
                        }
                        catch (NameClashException clash)
                        {
                            // Nothing of it is stored: its bytes are read past, so the other files of a bundle go on
                            await part.Body.CopyToAsync(Stream.Null, ct);
                            return new ReceivedPart(null, overwrite, clash);
                        }
                    }

                    return new ReceivedPart(await _uploads.StreamAsync(ctx, part.Body, ct), overwrite, null);
                }, received =>
                {
                    if (received.File != null) _uploads.Discard(received.File);
                });
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
            var folder = await _commands.CreateFolderAsync(orgSlug, dsSlug, form.GetField("path"), ct: ct);
            return Created(orgSlug, dsSlug, folder);
        }

        if (form.Files.Count == 1)
        {
            var (path, received) = form.Files[0];
            if (received.Refusal != null)
            {
                await _uploads.FailAsync(ctx, received.Refusal);
                ExceptionDispatchInfo.Throw(received.Refusal);
            }

            var entry = await _uploads.CommitAsync(ctx, path ?? form.GetField("path") ?? string.Empty, received.File!,
                received.Overwrite);
            return Created(orgSlug, dsSlug, entry);
        }

        var parts = form.Files.Select(f => new StreamedPart(f.Path ?? string.Empty,
            f.File.File ?? NotStored, f.File.Overwrite, f.File.Refusal)).ToArray();
        return Respond(orgSlug, dsSlug, await _uploads.CommitAsync(ctx, parts), bundle: true);
    }

    // The file of a part refused before its first byte: nothing on disk (the commit deletes no temp file of it)
    private static readonly StreamedFile NotStored =
        new(string.Empty, 0, new FileHashHint(string.Empty, 0, DateTime.MinValue));

    /// <summary>A file part of an upload request: its temp file, or why it was refused before its first byte.</summary>
    private sealed record ReceivedPart(StreamedFile? File, bool Overwrite, Exception? Refusal);

    /// <summary>
    /// The state of an upload by the <c>X-Upload-Id</c> it was sent with. A client whose request
    /// ended without a response (e.g. a proxy timeout after the last byte) asks here before sending
    /// the file again.
    /// </summary>
    /// <returns>200 with the receipt when the upload is done or failed; 202 while it is still being
    /// received or added; 404 when the id is unknown (send the file again).</returns>
    [HttpGet(RoutesHelper.ObjectsRadix + "/uploads/{uploadId}", Name = nameof(ObjectUploadsController) + "." + nameof(GetUpload))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UploadReceipt), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UploadReceipt>> GetUpload([FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, [FromRoute, Required] string uploadId, CancellationToken ct)
    {
        // The commit of a chunked upload keeps its receipt under the caller's session of the file id
        var receipt = await _uploads.GetReceiptAsync(orgSlug, dsSlug, uploadId, ct: ct)
                      ?? await _chunks.GetCommitReceiptAsync(orgSlug, dsSlug, uploadId, ct);
        Response.Headers.CacheControl = "no-store";

        return receipt switch
        {
            null => throw new NotFoundException($"Unknown upload '{uploadId}'"),
            { State: UploadReceiptState.Receiving or UploadReceiptState.Committing } => Accepted(receipt),
            _ => receipt
        };
    }

    /// <summary>
    /// Stores one chunk of a resumable upload (files larger than a single request should carry).
    /// The form fields <c>fileId</c>, <c>chunkIndex</c>, <c>totalChunks</c>, <c>totalFileSize</c>,
    /// <c>chunkSize</c> and <c>path</c> come before the <c>chunk</c> file part, which is streamed
    /// straight into the file at its offset. Chunks can be sent in any order and again; the first
    /// one checks access, path, name and quota. Commit with <c>POST obj/chunk/{fileId}/commit</c>.
    /// </summary>
    /// <remarks>
    /// The <c>overwrite</c> field of the first chunk is the upload's: without <c>overwrite=true</c> a file whose name
    /// exists is refused with <c>409</c> and <c>conflictPath</c> before its first byte is stored, and again at the
    /// commit when another upload wrote the name in the meantime.
    /// </remarks>
    [HttpPost(RoutesHelper.ObjectsRadix + "/chunk", Name = nameof(ObjectUploadsController) + "." + nameof(PostChunk))]
    [TypeFilter(typeof(UploadAnswerFilter))]
    [DisableFormValueModelBinding]
    [StreamedRequestBody(typeof(DatasetChunkForm))]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [ConfigurableUploadSizeLimit]
    [EnforceContentLength(ContentLengthLimit.Chunk)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status507InsufficientStorage)]
    public async Task<ActionResult<ChunkStatusDto>> PostChunk([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType ?? string.Empty))
            throw new BadRequestException("Expected a multipart/form-data request");

        ChunkStatusDto? status = null;
        await MultipartRequestHelper.ReadFilesFormAsync(Request, _formOptions.MultipartBoundaryLengthLimit, 1,
            async part =>
            {
                status = await _chunks.WriteChunkAsync(orgSlug, dsSlug, ChunkFields.ToDatasetChunk(part.Fields), part.Body,
                    ct);
                return status;
            }, _ => { /* a chunk is stored by the chunk store, which owns it */ });

        return status ?? throw new BadRequestException("No chunk uploaded");
    }

    /// <summary>The open resumable uploads of the caller in the dataset.</summary>
    /// <remarks>
    /// The Hub offers to resume them after a reload. For each: its <c>fileId</c>, path, size, source file name,
    /// <c>lastModified</c> and the chunks stored. They are selected by the <c>UserId</c> of the upload manifest; a
    /// session of another user is not listed.
    /// </remarks>
    [HttpGet(RoutesHelper.ObjectsRadix + "/chunk", Name = nameof(ObjectUploadsController) + "." + nameof(ListChunks))]
    [ProducesResponseType(typeof(ChunkStatusDto[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChunkStatusDto[]>> ListChunks([FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await _chunks.ListAsync(orgSlug, dsSlug, ct);
    }

    /// <summary>Which chunks of a resumable upload are stored and which are missing.</summary>
    /// <remarks>
    /// <c>404</c> for an unknown upload and for a session of another user: an upload is keyed by the caller and the
    /// <c>fileId</c>.
    /// </remarks>
    [HttpGet(RoutesHelper.ObjectsRadix + "/chunk/{fileId}", Name = nameof(ObjectUploadsController) + "." + nameof(GetChunks))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChunkStatusDto>> GetChunks([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string fileId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await _chunks.GetStatusAsync(orgSlug, dsSlug, fileId, ct: ct);
    }

    /// <summary>Removes the stored chunks of a resumable upload.</summary>
    /// <remarks>
    /// The Cancel of the Hub: the files that the same batch already sent stay. <c>404</c> for an unknown upload and for a
    /// session of another user.
    /// </remarks>
    [HttpDelete(RoutesHelper.ObjectsRadix + "/chunk/{fileId}", Name = nameof(ObjectUploadsController) + "." + nameof(DeleteChunks))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteChunks([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string fileId, CancellationToken ct)
    {
        await _chunks.DeleteAsync(orgSlug, dsSlug, fileId, ct);
        return NoContent();
    }

    /// <summary>
    /// Adds a resumable upload whose chunks are all stored to the dataset. Idempotent: committing a
    /// committed upload again returns its entry. 409 lists the missing chunks, or (with
    /// <c>conflictPath</c>) refuses a name that exists; the chunks stay, and the same commit with the form
    /// field <c>overwrite=true</c> replaces the file without sending them again.
    /// </summary>
    [HttpPost(RoutesHelper.ObjectsRadix + "/chunk/{fileId}/commit", Name = nameof(ObjectUploadsController) + "." + nameof(CommitChunks))]
    [TypeFilter(typeof(UploadAnswerFilter))]
    [DisableFormValueModelBinding]
    [StreamedRequestBody(typeof(ChunkCommitForm), Required = false)]
    [ProducesResponseType(typeof(EntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EntryDto>> CommitChunks([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string fileId, CancellationToken ct)
    {
        return Created(orgSlug, dsSlug,
            await _chunks.CommitAsync(orgSlug, dsSlug, fileId, await CommitOverwriteAsync(ct), ct));
    }

    // The body of a commit is optional: a form with overwrite, an empty form (a browser's empty FormData), or nothing
    private async Task<bool> CommitOverwriteAsync(CancellationToken ct)
    {
        if (!Request.HasFormContentType || Request.ContentLength == 0)
            return false;
        try
        {
            return ChunkFields.Overwrite((await Request.ReadFormAsync(ct))["overwrite"].FirstOrDefault());
        }
        catch (InvalidDataException)
        {
            // A multipart body without any part: no field, no flag
            return false;
        }
    }

    private IActionResult Respond(string orgSlug, string dsSlug, IReadOnlyList<UploadPartResultDto> results, bool bundle)
    {
        if (bundle)
            return Ok(results);

        var single = results[0];
        if (single.Entry != null)
            return Created(orgSlug, dsSlug, single.Entry);

        var problem = _errors.Create(HttpContext, single.Status, single.Error ?? "Upload failed",
            noRetry: UploadRetryRule.IsFinalStatus(single.Status));
        if (single.ConflictPath != null)
            problem.Extensions[ApiErrorResponder.ConflictPathMember] = single.ConflictPath;
        return ApiErrorResponder.ToActionResult(problem);
    }

    private CreatedAtRouteResult Created(string orgSlug, string dsSlug, EntryDto entry) =>
        CreatedAtRoute(nameof(ObjectsController) + "." + nameof(ObjectsController.GetInfo),
            new { orgSlug, dsSlug, path = entry.Path }, entry);
}
