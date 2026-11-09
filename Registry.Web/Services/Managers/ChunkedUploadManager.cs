using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
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
/// <remarks>
/// A session is keyed by the caller and the file id the client gave (§7.1): the store folder and the commit receipt are
/// named by <see cref="SessionKey"/>, so two users that send the same file id have two sessions, and a session of
/// another user does not exist for the caller (404). The Hub makes the file id from the dataset, the path, the size and
/// the last-modified time of the file, so the same file selected again after a reload has the same id.
/// </remarks>
public sealed class ChunkedUploadManager(UploadManager uploads, ChunkStore store, UploadQuotaGuard quota,
    ScratchFileProvider scratch, RegistryContext context)
{
    /// <summary>Folder of the chunked uploads of a dataset, under its uploads folder.</summary>
    public static string ChunksRoot(IDDB ddb) =>
        Path.Combine(ddb.DatasetFolderPath, IDDB.UploadsFolderName, DatasetUploads.ChunksFolderName);

    /// <summary>
    /// The name of the session of <paramref name="fileId"/> for <paramref name="userId"/> in the store and in the
    /// receipts: 64 hexadecimal characters of the SHA-256 of both. Another user's id gives another session.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.BadRequestException">The file id is not 8 to 64 letters, digits, '-' or '_'.</exception>
    public static string SessionKey(string? userId, string fileId)
    {
        if (!ChunkStore.IsValidFileId(fileId))
            throw new BadRequestException("Invalid file id: use 8 to 64 letters, digits, '-' or '_'");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{fileId}")));
    }

    /// <summary>
    /// Stores a chunk; the first chunk of an upload checks access, path, name and quota and fixes the path, the
    /// <c>overwrite</c> flag, the source file name and its last-modified time (later chunks do not change them, and a
    /// resumed upload goes on at the stored path): without the flag a name that exists is refused (D-040).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.NameClashException">The first chunk of a file whose name exists,
    /// without overwrite.</exception>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">The session exists for another file (size or chunks).</exception>
    public async Task<ChunkStatusDto> WriteChunkAsync(string orgSlug, string dsSlug, ChunkRequest chunk, Stream body,
        CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var key = SessionKey(ctx.User?.Id, chunk.FileId);
        var root = ChunksRoot(ctx.Ddb);

        var status = store.GetStatus(root, key);
        if (status == null)
        {
            // A new upload: refuse it before its first byte if it cannot be stored. The open uploads of the organizations
            // of the owner hold room for their whole files, in the quota of the owner, until they are committed or
            // removed (7 days): the new one must fit with them (FR-XC-26, D-039)
            uploads.ResolveTarget(ctx, chunk.Path, chunk.Overwrite);
            var open = await OpenUploadBytesAsync(ctx.Ddb, ctx.QuotaOwnerId, ctx.User?.Id, chunk.FileId, ct);
            using (await quota.ReserveAsync(ctx.QuotaOwnerId, chunk.TotalFileSize + open)) { }

            // The same file sent again after a commit has the same id: the receipt of the earlier one must not answer
            // the commit of this one
            await uploads.ForgetReceiptAsync(orgSlug, dsSlug, key);
            store.Open(root, key, new ChunkManifest(chunk.Path, chunk.TotalFileSize, chunk.TotalChunks, chunk.ChunkSize,
                ctx.User?.Id, chunk.Overwrite, chunk.FileId, chunk.SourceFileName, chunk.LastModified));
        }
        else
        {
            // A session that exists is not started again: one removed meanwhile (its Cancel) refuses the chunk (404)
            EnsureOwner(ctx, status.Manifest, chunk.FileId);
            if (status.Manifest.TotalSize != chunk.TotalFileSize || status.Manifest.TotalChunks != chunk.TotalChunks ||
                status.Manifest.ChunkSize != chunk.ChunkSize)
                throw new ConflictException($"Upload '{chunk.FileId}' exists with another file or owner");
        }

        return ToDto(await store.WriteAsync(root, key, chunk.ChunkIndex, body, ct));
    }

    /// <summary>
    /// The size of the files of the open chunked uploads of every user in the organizations of the owner
    /// <paramref name="ownerId"/> (every dataset folder of each of them; the organization of <paramref name="ddb"/> is one):
    /// the storage that they hold, in the quota of the owner, which the upload ledger reserves per owner, until they are
    /// committed or removed, which the storage use of the owner does not count yet. The session of
    /// <paramref name="userId"/> and <paramref name="fileId"/> in <paramref name="ddb"/> is not counted: it is the upload
    /// being checked (two first chunks of one file sent at once: the second must not count the session the first one
    /// just started). Without an owner no limit applies: 0.
    /// </summary>
    private async Task<long> OpenUploadBytesAsync(IDDB ddb, string ownerId, string? userId, string fileId,
        CancellationToken ct)
    {
        // No limit (no owner, limiter off, administrator owner): nothing to count, and no folder scan on every first chunk
        if (string.IsNullOrEmpty(ownerId) || !await quota.LimitAppliesAsync(ownerId, ct))
            return 0;

        // The folder of a dataset is <datasets>/<organization slug>/<dataset> (DdbManager.GetDatasetFolder)
        var dataset = Path.GetFullPath(ddb.DatasetFolderPath);
        var organization = Path.GetDirectoryName(dataset);
        var datasets = organization == null ? null : Path.GetDirectoryName(organization);
        if (organization == null || datasets == null)
            return 0;

        var owned = await context.Organizations.AsNoTracking()
            .Where(org => org.OwnerId == ownerId)
            .Select(org => org.Slug)
            .ToListAsync(ct);
        var organizations = owned.Select(slug => Path.GetFullPath(Path.Combine(datasets, slug)))
            .Append(organization)
            .Distinct(PathComparison.Comparer)
            .Where(Directory.Exists);

        // In one dataset the session of a user and a file id is one folder (SessionKey): its manifest has both
        bool Current(string folder, ChunkStatus s) =>
            s.Manifest.FileId == fileId && s.Manifest.UserId == userId && Path.GetFullPath(folder) == dataset;

        return organizations
            .SelectMany(Directory.EnumerateDirectories)
            .SelectMany(folder => store.List(Path.Combine(folder, IDDB.UploadsFolderName, DatasetUploads.ChunksFolderName))
                .Where(s => !Current(folder, s)))
            .Sum(s => s.Manifest.TotalSize);
    }

    /// <summary>The state of an upload of the caller.</summary>
    /// <exception cref="Registry.Web.Exceptions.NotFoundException">Unknown upload, or one of another user.</exception>
    public async Task<ChunkStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string fileId,
        CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var status = store.GetStatus(ChunksRoot(ctx.Ddb), SessionKey(ctx.User?.Id, fileId))
                     ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        EnsureOwner(ctx, status.Manifest, fileId);
        return ToDto(status);
    }

    /// <summary>
    /// The receipt of the commit of a chunked upload of the caller, by the file id the client knows (a client whose
    /// commit lost its answer asks for it, as for any upload id); null when unknown.
    /// </summary>
    public async Task<UploadReceipt?> GetCommitReceiptAsync(string orgSlug, string dsSlug, string fileId,
        CancellationToken ct = default) =>
        ChunkStore.IsValidFileId(fileId)
            ? await uploads.GetReceiptAsync(orgSlug, dsSlug, SessionKey(await uploads.CurrentUserIdAsync(), fileId), ct)
            : null;

    /// <summary>
    /// The open uploads of the caller in the dataset, selected by the <c>UserId</c> of the upload manifest (§7.1): the
    /// Hub offers to resume them after a reload. An upload made before the manifest kept its file id is not listed.
    /// </summary>
    public async Task<ChunkStatusDto[]> ListAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        return store.List(ChunksRoot(ctx.Ddb))
            .Where(s => s.Manifest.FileId != null && s.Manifest.UserId == ctx.User?.Id)
            .OrderBy(s => s.Manifest.Path, StringComparer.Ordinal)
            .Select(ToDto)
            .ToArray();
    }

    /// <summary>Removes the stored chunks of an upload of the caller (its Cancel).</summary>
    /// <exception cref="Registry.Web.Exceptions.NotFoundException">Unknown upload, or one of another user.</exception>
    public async Task DeleteAsync(string orgSlug, string dsSlug, string fileId, CancellationToken ct = default)
    {
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, ct: ct);
        var root = ChunksRoot(ctx.Ddb);
        var key = SessionKey(ctx.User?.Id, fileId);
        var status = store.GetStatus(root, key) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        EnsureOwner(ctx, status.Manifest, fileId);

        if (!await store.DeleteAsync(root, key))
            throw new NotFoundException($"Unknown upload '{fileId}'");
        await uploads.ForgetReceiptAsync(orgSlug, dsSlug, key);
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
        using var ctx = await uploads.BeginAsync(orgSlug, dsSlug, uploadId: SessionKey(await uploads.CurrentUserIdAsync(), fileId),
            ct: ct);
        if (ctx.Receipt?.Results is { Count: > 0 } done && done[0].Entry is { } entry)
            return entry;

        try
        {
            var root = ChunksRoot(ctx.Ddb);
            var key = ctx.UploadId!;
            var status = store.GetStatus(root, key) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
            EnsureOwner(ctx, status.Manifest, fileId);
            var replace = overwrite || status.Manifest.Overwrite;
            if (status.Complete)
                uploads.ResolveTarget(ctx, status.Manifest.Path, replace);

            // The chunks were written into one file: taking it is a rename onto the dataset volume
            using var temp = scratch.CreateForDataset(ctx.Ddb.DatasetFolderPath, DatasetUploads.TempFileExtension);
            var hint = await store.TakeAsync(root, key, temp.Path);
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
        // A session of another user does not exist for the caller (§7.1)
        if (manifest.UserId != ctx.User?.Id)
            throw new NotFoundException($"Unknown upload '{fileId}'");
    }

    // The file id is the client's own: the manifest keeps it, since the store names the session by its key
    private static ChunkStatusDto ToDto(ChunkStatus status) => new()
    {
        FileId = status.Manifest.FileId ?? string.Empty,
        Path = status.Manifest.Path,
        SourceFileName = status.Manifest.SourceFileName,
        LastModified = status.Manifest.LastModified,
        TotalFileSize = status.Manifest.TotalSize,
        TotalChunks = status.Manifest.TotalChunks,
        ChunkSize = status.Manifest.ChunkSize,
        Received = status.Received.ToArray(),
        Missing = status.Missing.ToArray(),
        Complete = status.Complete
    };
}
