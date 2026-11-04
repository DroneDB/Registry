using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Push;

/// <summary>The files and metadata of a push: streamed against the storage quota, checked against the hash of the delta, staged in the push folder.</summary>
public sealed class PushUploads
{
    private readonly IDatasetAccessResolver _access;
    private readonly IAuthManager _authManager;
    private readonly UploadQuotaGuard _quota;
    private readonly ScratchFileProvider _scratch;
    private readonly IFileSystem _fileSystem;
    private readonly PushSessionStore _sessionStore;
    private readonly DatasetPathPolicy _paths;

    public PushUploads(IDatasetAccessResolver access, IAuthManager authManager, UploadQuotaGuard quota, ScratchFileProvider scratch, IFileSystem fileSystem, PushSessionStore sessionStore, DatasetPathPolicy paths)
    {
        _access = access;
        _authManager = authManager;
        _quota = quota;
        _scratch = scratch;
        _fileSystem = fileSystem;
        _sessionStore = sessionStore;
        _paths = paths;
    }

    public async Task<PushUpload> StreamUploadAsync(string orgSlug, string dsSlug, Stream source,
        long? expectedBytes, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        // Reserved against one storage evaluation per window, as uploads are (the files of a push
        // add up): before writing when the size is known
        var userId = await _authManager.GetCurrentUserId() ?? string.Empty;
        var reservation = expectedBytes is { } expected ? await _quota.ReserveAsync(userId, expected) : null;
        try
        {
            var ddb = access.Ddb;
            var (tempPath, bytes, hash) =
                await DatasetUploads.StreamToTempAsync(_scratch, _fileSystem, ddb.DatasetFolderPath, source, ct);

            try
            {
                if (reservation == null)
                    reservation = await _quota.ReserveAsync(userId, bytes);
                else
                    reservation.Resize(bytes);
            }
            catch
            {
                _fileSystem.Delete(tempPath);
                throw;
            }

            // On disk until the push commits or is abandoned: it counts as stored
            reservation.Commit();
            return new PushUpload(ddb, tempPath, bytes, hash);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    public async Task UploadAsync(PushUpload upload, string? path, string? token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        // Honoured until the file is staged
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
            throw new BadRequestException("Path cannot be empty");

        var baseTempFolder = PushFolders.Of(upload.Ddb, token);

        var session = await _sessionStore.LoadAsync(baseTempFolder) ??
                      throw new ConflictException("Cannot upload file before initializing push");

        // The entry path of the delta: libddb's path rule, as for any upload
        path = _paths.Normalize(path);

        // The hash computed while the file streamed must be the delta's for that path; a
        // file the delta does not add, or whose bytes differ, is refused before it is staged
        var expected = session.AddedFileHash(path) ??
                       throw new BadRequestException($"'{path}' is not a file of this push");
        if (!string.Equals(expected, upload.Hash.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException(
                $"'{path}' does not match its hash in the push (the upload is corrupt): upload it again");

        // Move the streamed file (same volume) to its place in the push folder
        var filePath = DatasetPathPolicy.ToLocalPath(PushFolders.Adds(baseTempFolder), path);
        _fileSystem.EnsureParentFolderExists(filePath);
        if (_fileSystem.Exists(filePath))
            _fileSystem.Delete(filePath);
        _fileSystem.Move(upload.TempPath, filePath);

        // Commit hands this hash to libddb, which then does not read the file again
        await _sessionStore.MarkVerifiedAsync(baseTempFolder, path, expected, filePath);
    }

    public async Task SaveMeta(string orgSlug, string dsSlug, string token, string meta, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        var ddb = access.Ddb;

        var baseTempFolder = PushFolders.Of(ddb, token);

        if (!_sessionStore.Exists(baseTempFolder))
            throw new ConflictException("Cannot save meta before initializing push");

        var metaFile = PushFolders.Meta(baseTempFolder);

        // Validate
        ApiJson.Deserialize<List<MetaDump>>(meta);

        // Save file
        await _fileSystem.WriteAllTextAsync(metaFile, meta, CancellationToken.None);
    }
}
