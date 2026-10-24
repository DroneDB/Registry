#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Ports;

/// <summary>A file part of an upload request, already on disk.</summary>
/// <param name="Path">Destination path in the dataset.</param>
/// <param name="File">The streamed temp file.</param>
public sealed record StreamedPart(string Path, StreamedFile File);

/// <summary>
/// The upload pipeline (UPL-01): one context per request, pre-checks before the first byte,
/// hashing while streaming, one index batch, quota reservation and cache invalidation per request,
/// and receipts for clients that lost the response.
/// </summary>
public interface IUploadManager
{
    /// <summary>
    /// Resolves the dataset, checks write access, reserves <paramref name="expectedBytes"/> of quota
    /// and registers the upload: everything that can refuse it before its first byte. With an
    /// <paramref name="uploadId"/> whose upload is already done, <see cref="UploadRequestContext.Receipt"/>
    /// holds its results and nothing else must be done.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">An upload with that id is still running.</exception>
    Task<UploadRequestContext> BeginAsync(string orgSlug, string dsSlug, long? expectedBytes = null,
        string? uploadId = null, CancellationToken ct = default);

    /// <summary>
    /// The normalized dataset path a file uploaded to <paramref name="path"/> is stored at; throws when
    /// no file can be uploaded there (before streaming it).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.InvalidDatasetPathException">Not a dataset path (NUL, absolute, "..", reserved).</exception>
    /// <exception cref="Registry.Web.Exceptions.ExtensionBlockedException">The import policy blocks the extension.</exception>
    string ValidatePath(UploadRequestContext ctx, string path);

    /// <summary>Streams one file part to a temp file in the dataset, hashing it on the way.</summary>
    Task<StreamedFile> StreamAsync(UploadRequestContext ctx, Stream body, CancellationToken ct = default);

    /// <summary>Commits a single file: moves it into place and indexes it. Throws on failure.</summary>
    Task<EntryDto> CommitAsync(UploadRequestContext ctx, string path, StreamedFile file);

    /// <summary>
    /// Commits several files with one quota reservation, one index batch and one invalidation.
    /// Each file gets its own result; one that fails does not fail the others.
    /// </summary>
    Task<IReadOnlyList<UploadPartResultDto>> CommitAsync(UploadRequestContext ctx, IReadOnlyList<StreamedPart> parts);

    /// <summary>Deletes the temp file of a part that will not be committed (the request failed while reading it).</summary>
    void Discard(StreamedFile file);

    /// <summary>Records that the request failed before its files were committed.</summary>
    Task FailAsync(UploadRequestContext ctx, System.Exception error);

    /// <summary>The outcome of an upload by its id, or null when unknown (write access required).</summary>
    Task<UploadReceipt?> GetReceiptAsync(string orgSlug, string dsSlug, string uploadId, CancellationToken ct = default);
}
