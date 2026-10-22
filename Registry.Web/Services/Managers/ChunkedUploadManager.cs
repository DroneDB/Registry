#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IChunkedUploadManager"/>: chunks are stored by <see cref="IChunkStore"/>
/// under the dataset's uploads folder (<c>.uploads/chunks</c>, same volume as the dataset), and
/// the finished file goes through the same commit as any upload (<see cref="IUploadManager"/>).
/// </summary>
public sealed class ChunkedUploadManager(IUploadManager uploads, IChunkStore store, IUploadQuotaGuard quota,
    IScratchFileProvider scratch)
    : IChunkedUploadManager
{
    /// <summary>Folder of the chunked uploads of a dataset, under its uploads folder.</summary>
    public static string ChunksRoot(IDDB ddb) =>
        Path.Combine(ddb.DatasetFolderPath, IDDB.UploadsFolderName, DatasetUploads.ChunksFolderName);

    public async Task<ChunkStatusDto> WriteChunkAsync(string orgSlug, string dsSlug, ChunkRequest chunk, Stream body,
        CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var root = ChunksRoot(ctx.Ddb);
        var manifest = new ChunkManifest(chunk.Path, chunk.TotalFileSize, chunk.TotalChunks, chunk.ChunkSize, ctx.User?.Id);

        var status = store.GetStatus(root, chunk.FileId);
        if (status == null)
        {
            // A new upload: refuse it before its first byte if it cannot be stored
            uploads.ValidatePath(ctx, chunk.Path);
            using (await quota.ReserveAsync(ctx.User?.Id ?? string.Empty, chunk.TotalFileSize)) { }
        }
        else
        {
            EnsureOwner(ctx, status.Manifest, chunk.FileId);
        }

        store.Open(root, chunk.FileId, manifest);
        return ToDto(chunk.FileId, await store.WriteAsync(root, chunk.FileId, chunk.ChunkIndex, body, ct));
    }

    public async Task<ChunkStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string fileId,
        CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var status = store.GetStatus(ChunksRoot(ctx.Ddb), fileId)
                     ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        EnsureOwner(ctx, status.Manifest, fileId);
        return ToDto(fileId, status);
    }

    public async Task<EntryDto> CommitAsync(string orgSlug, string dsSlug, string fileId,
        CancellationToken ct = default)
    {
        // Keyed by the file id: committing again (the response was lost) answers from the receipt. Honoured
        // while the upload is resolved; the commit then runs to completion (RQ-13)
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, uploadId: fileId, ct: ct);
        if (ctx.Receipt?.Results is { Count: > 0 } done && done[0].Entry is { } entry)
            return entry;

        try
        {
            var root = ChunksRoot(ctx.Ddb);
            var status = store.GetStatus(root, fileId) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
            EnsureOwner(ctx, status.Manifest, fileId);

            // The chunks were written into one file: taking it is a rename onto the dataset volume
            using var temp = scratch.CreateForDataset(ctx.Ddb.DatasetFolderPath, DatasetUploads.TempFileExtension);
            var hint = await store.TakeAsync(root, fileId, temp.Path);
            temp.Release(); // the commit moves it into place or deletes it

            return await uploads.CommitAsync(ctx, status.Manifest.Path, new StreamedFile(temp.Path, hint.Size, hint));
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
