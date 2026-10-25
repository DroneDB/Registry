#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>Default <see cref="IEntryTransferService"/>.</summary>
public sealed class EntryTransferService : IEntryTransferService
{
    private readonly IDatasetAccessResolver _access;
    private readonly DatasetPathPolicy _paths;
    private readonly IFileSystem _fs;
    private readonly EntryWritePolicy _writePolicy;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly EntryChangeNotifier _changes;
    private readonly ILogger<EntryTransferService> _logger;

    public EntryTransferService(IDatasetAccessResolver access, DatasetPathPolicy paths, IFileSystem fs,
        EntryWritePolicy writePolicy, IBuildArtifactResolver artifacts, EntryChangeNotifier changes,
        ILogger<EntryTransferService> logger)
    {
        _access = access;
        _paths = paths;
        _fs = fs;
        _writePolicy = writePolicy;
        _artifacts = artifacts;
        _changes = changes;
        _logger = logger;
    }

    public async Task TransferAsync(string sourceOrgSlug, string sourceDsSlug, string sourcePath, string destOrgSlug,
        string destDsSlug, string? destPath = null, bool overwrite = false, bool keepSource = false,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Transfer '{SourceOrgSlug}/{SourceDsSlug}, {SourcePath}' -> '{DestOrgSlug}/{DestDsSlug}', {DestPath}; overwrite={Overwrite}; keepSource={KeepSource}",
            sourceOrgSlug, sourceDsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite, keepSource);

        if (sourceOrgSlug == destOrgSlug && sourceDsSlug == destDsSlug)
            throw new BadRequestException("Source and destination cannot be the same");

        var sourceDdb = (await _access.ResolveAsync(sourceOrgSlug, sourceDsSlug, AccessType.Read, ct: ct)).Ddb;
        var destDdb = (await _access.ResolveAsync(destOrgSlug, destDsSlug, AccessType.Write, ct: ct)).Ddb;

        var sourceEntry = sourceDdb.GetEntry(sourcePath)
                          ?? throw new NotFoundException("Cannot find source entry: '" + sourcePath + "'");
        var isFolder = sourceEntry.Type == EntryType.Directory;
        var files = FilesOf(sourceDdb, sourceEntry, sourcePath);

        EnsureNoLiveBuild(sourceDdb, sourceEntry, sourcePath, files);

        // Without a destination: the source name in the root of the destination dataset
        if (string.IsNullOrWhiteSpace(destPath))
        {
            destPath = Path.GetFileName(sourcePath);
            _logger.LogInformation("Destination path not specified, using source name: '{DestPath}'", destPath);
        }

        // libddb's path rule: no traversal, no absolute path, not reserved
        destPath = _paths.Normalize(destPath, "destination path");

        await _writePolicy.EnsureAllowedAsync(destPath, isFolder, await GetEntrySizeAsync(sourceDdb, sourceEntry));

        if (destDdb.GetEntry(destPath) is { } destEntry)
        {
            if (!overwrite)
                throw new ConflictException("Destination entry already exists and overwrite is false");
            if (isFolder != (destEntry.Type == EntryType.Directory))
                throw new BadRequestException(isFolder ? "Cannot transfer a folder on a file" : "Cannot transfer a file on a folder");
        }

        if (sourceEntry.Type == EntryType.DroneDB)
            throw new BadRequestException("Cannot transfer a DroneDB file");

        // Track progress for rollback
        var destLocalPath = destDdb.GetLocalPath(destPath);
        var fileSystemCopied = false;
        var addedToDdb = false;

        try
        {
            _logger.LogInformation("Transferring {Kind} '{Source}' to '{Dest}'", isFolder ? "directory" : "file",
                sourcePath, destPath);
            _fs.EnsureParentFolderExists(destLocalPath);
            _fs.CopyEntry(sourceDdb.GetLocalPath(sourcePath), destLocalPath, isFolder);
            fileSystemCopied = true;

            // The copy is indexed with the source hashes, not hashed again
            CopiedEntries.Index(sourceDdb, sourceEntry, destDdb, destPath, _fs);
            addedToDdb = true;

            if (!destDdb.EntryExists(destPath))
                throw new DdbException($"Cannot find destination '{destPath}' after transfer, something wrong with ddb");

            // The builds of the transferred files (artifacts and pending markers, DDBCopyBuildArtifacts), so the
            // destination does not build them again; copied, never moved: a build may serve other entries with
            // the same content. A failure below leaves them as orphans for the recurring cleanup.
            var copiedBuilds = _artifacts.CopyBuilds(sourceDdb, files, destDdb);
            _logger.LogInformation("Copied {Count} build(s) of {Files} file(s) to the destination", copiedBuilds,
                files.Length);

            // Move semantics: the index entry, then the files (the index only forgets them)
            if (!keepSource)
            {
                _logger.LogInformation("Removing source '{SourcePath}'", sourcePath);
                sourceDdb.Remove(sourcePath);
                _fs.RemoveEntry(sourceDdb.GetLocalPath(sourcePath), isFolder);
                await _changes.EntriesChangedAsync(sourceOrgSlug, sourceDsSlug, [sourcePath]);
            }

            await _changes.EntriesChangedAsync(destOrgSlug, destDsSlug, [destPath]);

            _logger.LogInformation("Transfer OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer failed, attempting rollback");

            // Rollback in reverse order
            if (addedToDdb)
                Undo("DDB add", () => destDdb.Remove(destPath));
            if (fileSystemCopied)
                Undo("file system copy", () => _fs.RemoveEntry(destLocalPath, isFolder));

            throw;
        }
    }

    public async Task<EntryDto> CopyAsync(string orgSlug, string dsSlug, string source, string dest,
        bool overwrite = false, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct)).Ddb;

        _logger.LogInformation("Copy in '{OrgSlug}/{DsSlug}': '{Source}' -> '{Dest}', overwrite={Overwrite}",
            orgSlug, dsSlug, source, dest, overwrite);

        if (string.IsNullOrWhiteSpace(source))
            throw new BadRequestException("Source path is required");
        if (string.IsNullOrWhiteSpace(dest))
            throw new BadRequestException("Destination path is required");

        // libddb's path rule: no traversal, no absolute path, not reserved
        source = _paths.Normalize(source, "source path");
        dest = _paths.Normalize(dest, "destination path");

        if (source == dest)
            throw new BadRequestException("Source and destination cannot be the same");

        // Cannot copy a folder into itself or one of its descendants
        if ((dest + "/").StartsWith(source + "/", StringComparison.Ordinal))
            throw new BadRequestException("Cannot copy a path onto itself or one of its descendants");

        var sourceEntry = ddb.GetEntry(source) ?? throw new NotFoundException("Cannot find source entry: '" + source + "'");

        if (sourceEntry.Type == EntryType.DroneDB)
            throw new BadRequestException("Cannot copy a DroneDB file");

        var isFolder = sourceEntry.Type == EntryType.Directory;
        EnsureNoLiveBuild(ddb, sourceEntry, source, FilesOf(ddb, sourceEntry, source));

        var destEntry = ddb.GetEntry(dest);
        if (destEntry != null)
        {
            if (!overwrite)
                throw new ConflictException("Destination entry already exists and overwrite is false");
            if (isFolder != (destEntry.Type == EntryType.Directory))
                throw new BadRequestException(isFolder ? "Cannot copy a folder on a file" : "Cannot copy a file on a folder");
        }

        // When overwriting, the destination's size is freed by the operation: charge only the net growth
        // (at least 0), so content of the same size or smaller is never refused
        var entrySize = await GetEntrySizeAsync(ddb, sourceEntry);
        var storageDelta = destEntry == null ? entrySize : Math.Max(0L, entrySize - await GetEntrySizeAsync(ddb, destEntry));
        await _writePolicy.EnsureAllowedAsync(dest, isFolder, storageDelta);

        var sourceLocalPath = ddb.GetLocalPath(source);
        var destLocalPath = ddb.GetLocalPath(dest);
        var destWasFolder = destEntry?.Type == EntryType.Directory;
        string? tempCopyPath = null;
        string? trashPath = null;
        var copiedToTemp = false;
        var trashCreated = false;
        var fileSystemCopied = false;
        var destDdbAdded = false;

        try
        {
            _fs.EnsureParentFolderExists(destLocalPath);

            if (destEntry != null)
            {
                // Atomic overwrite: copy to a temporary sibling (the destination is intact), move the
                // destination to a trash sibling (kept for a rollback), promote the copy, index it; then the
                // trash is deleted, or put back when anything failed
                var token = Guid.NewGuid().ToString("N");
                tempCopyPath = destLocalPath + ".copying-" + token;
                trashPath = destLocalPath + ".trash-" + token;

                _logger.LogInformation("Atomic overwrite: copying '{Source}' -> '{Temp}'", source, tempCopyPath);
                _fs.CopyEntry(sourceLocalPath, tempCopyPath, isFolder);
                copiedToTemp = true;

                // The destination goes to the trash BEFORE its index entry is removed: when the move fails the
                // index still matches the disk and the rollback has nothing extra to undo
                _logger.LogInformation("Atomic overwrite: moving existing dest '{Dest}' -> trash '{Trash}'", dest,
                    trashPath);
                if (destWasFolder ? _fs.FolderExists(destLocalPath) : _fs.Exists(destLocalPath))
                    _fs.MoveEntry(destLocalPath, trashPath, destWasFolder);
                trashCreated = true;

                ddb.Remove(dest);

                _logger.LogInformation("Atomic overwrite: promoting temp '{Temp}' -> dest '{Dest}'", tempCopyPath, dest);
                _fs.MoveEntry(tempCopyPath, destLocalPath, isFolder);
                copiedToTemp = false;
            }
            else
            {
                _logger.LogInformation("Copying {Kind} '{Source}' to '{Dest}'", isFolder ? "directory" : "file", source,
                    dest);
                _fs.CopyEntry(sourceLocalPath, destLocalPath, isFolder);
            }

            fileSystemCopied = true;

            // The copy is indexed with the source hashes, not hashed again
            CopiedEntries.Index(ddb, sourceEntry, ddb, dest, _fs);
            destDdbAdded = true;

            var copiedEntry = ddb.GetEntry(dest)
                              ?? throw new DdbException($"Cannot find destination '{dest}' after copy, something wrong with ddb");

            // Commit: the previous destination is not needed any more (the new one is in place)
            if (trashCreated)
                Undo("trash copy", () => _fs.RemoveEntry(trashPath!, destWasFolder), LogLevel.Warning);

            // Builds are named by content hash: the copy's content is the source's, its build serves both

            await _changes.EntriesChangedAsync(orgSlug, dsSlug, [dest]);

            _logger.LogInformation("Copy OK");
            return copiedEntry.ToDto();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy failed, attempting rollback");

            if (destDdbAdded)
                Undo("DDB add", () => ddb.Remove(dest));

            // A partially promoted destination goes before the trash is put back
            if (fileSystemCopied)
                Undo("file system copy", () => _fs.RemoveEntry(destLocalPath, isFolder));

            // The temporary copy, when it was never promoted
            if (copiedToTemp)
                Undo("temp copy", () => _fs.RemoveEntry(tempCopyPath!, isFolder));

            // The original destination, when it was moved aside
            if (trashCreated && destEntry != null)
                RestoreFromTrash(ddb, dest, destLocalPath, trashPath!, destWasFolder);

            throw;
        }
    }

    // The files of an entry: the entry itself, or every file under a folder
    private static string[] FilesOf(IDDB ddb, Entry entry, string path) =>
        entry.Type == EntryType.Directory
            ? ddb.FindSummaries(EntryQuery.Under(path)).Where(item => item.Type != EntryType.Directory)
                .Select(item => item.Path).ToArray()
            : [path];

    /// <summary>
    /// Refuses to copy or transfer an entry while one of its files is being built: one manifest call for the
    /// file, or for every file of a folder.
    /// </summary>
    /// <exception cref="ConflictException">A live build holds the lock of one of the files.</exception>
    private void EnsureNoLiveBuild(IDDB ddb, Entry entry, string path, string[] files)
    {
        if (files.Length == 0)
            return;

        var building = _artifacts.GetBuilds(ddb, files).Where(b => b.HasLiveBuild()).Select(b => b.Path).ToArray();
        if (building.Length == 0)
            return;

        throw new ConflictException(entry.Type == EntryType.Directory
            ? $"Cannot transfer directory '{path}' because the following files have an active build in progress: {string.Join(", ", building)}"
            : $"Cannot transfer file '{path}' because it has an active build in progress");
    }

    /// <summary>The size of an entry: a file's from the index, a folder's from the disk (recursively).</summary>
    private async Task<long> GetEntrySizeAsync(IDDB ddb, Entry entry)
    {
        if (entry.Type != EntryType.Directory)
            return entry.Size;

        var localPath = ddb.GetLocalPath(entry.Path);
        if (!_fs.FolderExists(localPath))
        {
            _logger.LogWarning("Directory not found at '{LocalPath}', returning 0 size", localPath);
            return 0;
        }

        // A file system error is a server fault: it reaches the error handler (logged once, 500)
        return await Task.Run(() => _fs.GetDirectorySize(localPath));
    }

    private void RestoreFromTrash(IDDB ddb, string dest, string destLocalPath, string trashPath, bool wasFolder)
    {
        try
        {
            _logger.LogWarning("Restoring original destination '{Dest}' from trash '{Trash}'", dest, trashPath);
            if (wasFolder ? _fs.FolderExists(trashPath) : _fs.Exists(trashPath))
                _fs.MoveEntry(trashPath, destLocalPath, wasFolder);

            // Re-index the restored entry so the DDB stays consistent
            Undo("re-index of the restored destination", () => ddb.AddRaw(dest));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore original destination '{Dest}' from trash '{Trash}'", dest, trashPath);
        }
    }

    // A rollback step: logged when it fails, never thrown over the original failure
    private void Undo(string step, Action undo, LogLevel failureLevel = LogLevel.Error)
    {
        try
        {
            undo();
        }
        catch (Exception ex)
        {
            _logger.Log(failureLevel, ex, "Failed to roll back the {Step}", step);
        }
    }
}
