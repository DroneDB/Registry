using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Resumable uploads of large files into a dataset: the client sends the file in
/// chunks, asks which chunks are stored after any failure, sends only those, then commits once.
/// The commit is idempotent (keyed by the file id) and needs no pass over the file.
/// </summary>
public sealed class ChunkedUploadManager(UploadManager uploads, ChunkStore store, UploadQuotaGuard quota,
    ScratchFileProvider scratch)
{
    /// <summary>Folder of the chunked uploads of a dataset, under its uploads folder.</summary>
    public static string ChunksRoot(IDDB ddb) =>
        Path.Combine(ddb.DatasetFolderPath, IDDB.UploadsFolderName, DatasetUploads.ChunksFolderName);

    /// <summary>
    /// Stores a chunk; the first chunk of an upload checks access, path, name and quota. Its <c>overwrite</c> flag is
    /// the upload's (later chunks do not change it): without it a name that exists is refused (D-040).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.NameClashException">The first chunk of a file whose name exists,
    /// without overwrite.</exception>
    public async Task<ChunkStatusDto> WriteChunkAsync(string orgSlug, string dsSlug, ChunkRequest chunk, Stream body,
        CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var root = ChunksRoot(ctx.Ddb);

        var status = store.GetStatus(root, chunk.FileId);
        var manifest = new ChunkManifest(chunk.Path, chunk.TotalFileSize, chunk.TotalChunks, chunk.ChunkSize, ctx.User?.Id,
            status?.Manifest.Overwrite ?? chunk.Overwrite);
        if (status == null)
        {
            // A new upload: refuse it before its first byte if it cannot be stored
            uploads.ResolveTarget(ctx, chunk.Path, chunk.Overwrite);
            using (await quota.ReserveAsync(ctx.User?.Id ?? string.Empty, chunk.TotalFileSize)) { }
        }
        else
        {
            EnsureOwner(ctx, status.Manifest, chunk.FileId);
        }

        store.Open(root, chunk.FileId, manifest);
        return ToDto(chunk.FileId, await store.WriteAsync(root, chunk.FileId, chunk.ChunkIndex, body, ct));
    }

    /// <summary>The state of an upload.</summary>
    /// <exception cref="Registry.Web.Exceptions.NotFoundException">Unknown upload.</exception>
    public async Task<ChunkStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string fileId,
        CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var status = store.GetStatus(ChunksRoot(ctx.Ddb), fileId)
                     ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        EnsureOwner(ctx, status.Manifest, fileId);
        return ToDto(fileId, status);
    }

    /// <summary>
    /// Adds the finished file to the dataset; again for a committed upload, returns its entry. The name is checked
    /// again (another upload may have written it since the first chunk) before the chunks are taken, so a refused
    /// commit keeps them: the same commit with <paramref name="overwrite"/> (the user chose to replace) adds them
    /// without sending them again.
    /// </summary>
    /// <param name="orgSlug">The organization.</param>
    /// <param name="dsSlug">The dataset.</param>
    /// <param name="fileId">The upload.</param>
    /// <param name="overwrite">Replace a file that holds the name, also when the first chunk did not say so.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">Chunks are missing.</exception>
    /// <exception cref="Registry.Web.Exceptions.NameClashException">The name exists and the upload does not overwrite.</exception>
    public async Task<EntryDto> CommitAsync(string orgSlug, string dsSlug, string fileId, bool overwrite = false,
        CancellationToken ct = default)
    {
        // Keyed by the file id: committing again (the response was lost) answers from the receipt. Honoured
        // while the upload is resolved; the commit then runs to completion
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, uploadId: fileId, ct: ct);
        if (ctx.Receipt?.Results is { Count: > 0 } done && done[0].Entry is { } entry)
            return entry;

        try
        {
            var root = ChunksRoot(ctx.Ddb);
            var status = store.GetStatus(root, fileId) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
            EnsureOwner(ctx, status.Manifest, fileId);
            var replace = overwrite || status.Manifest.Overwrite;
            if (status.Complete)
                uploads.ResolveTarget(ctx, status.Manifest.Path, replace);

            // The chunks were written into one file: taking it is a rename onto the dataset volume
            using var temp = scratch.CreateForDataset(ctx.Ddb.DatasetFolderPath, DatasetUploads.TempFileExtension);
            var hint = await store.TakeAsync(root, fileId, temp.Path);
            temp.Release(); // the commit moves it into place or deletes it

            return await uploads.CommitAsync(ctx, status.Manifest.Path, new StreamedFile(temp.Path, hint.Size, hint),
                replace);
        }
        catch (Exception ex)
        {
            await uploads.FailAsync(ctx, ex);
            throw;
        }
    }

    private static void EnsureOwner(UploadRequestContext ctx, ChunkManifest manifest, string fileId)
    {
        if (manifest.UserId != ctx.User?.Id)
            throw new ConflictException($"Upload '{fileId}' belongs to another user");
    }

    private static ChunkStatusDto ToDto(string fileId, ChunkStatus status) => new()
    {
        FileId = fileId,
        Path = status.Manifest.Path,
        TotalFileSize = status.Manifest.TotalSize,
        TotalChunks = status.Manifest.TotalChunks,
        ChunkSize = status.Manifest.ChunkSize,
        Received = status.Received.ToArray(),
        Missing = status.Missing.ToArray(),
        Complete = status.Complete
    };
}
