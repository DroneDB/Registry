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

    // One per process, not per request (the service is scoped): the transfers of a dataset wait for each other
    private static readonly KeyedAsyncLock DatasetLocks = new();
    private readonly KeyedAsyncLock _datasetLocks = DatasetLocks;

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

        // A transfer that does not keep its source removes it from the source dataset: that takes write access to
        // the source, as a delete there does; reading it is enough only for a copy. Both checks come before anything
        // is written to the destination.
        var sourceAccess = keepSource ? AccessType.Read : AccessType.Write;
        var sourceDdb = (await _access.ResolveAsync(sourceOrgSlug, sourceDsSlug, sourceAccess, ct: ct)).Ddb;
        var destAccess = await _access.ResolveAsync(destOrgSlug, destDsSlug, AccessType.Write, ct: ct);
        var destDdb = destAccess.Ddb;

        // One transfer at a time in the datasets it reads and writes: two requests for the same entry never both
        // pass the checks below. Everything that depends on the state of the datasets is read under the lock.
        using var lease = await _datasetLocks.AcquireAsync(
            [LockKey(sourceOrgSlug, sourceDsSlug), LockKey(destOrgSlug, destDsSlug)], ct);

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

        await _writePolicy.EnsureAllowedAsync(destAccess.Dataset.Organization?.OwnerId, destPath, isFolder, await GetEntrySizeAsync(sourceDdb, sourceEntry));

        var destEntry = destDdb.GetEntry(destPath);
        if (destEntry != null)
        {
            if (!overwrite)
                throw new ConflictException("Destination entry already exists and overwrite is false");
            if (isFolder != (destEntry.Type == EntryType.Directory))
                throw new BadRequestException(isFolder ? "Cannot transfer a folder on a file" : "Cannot transfer a file on a folder");
        }

        if (sourceEntry.Type == EntryType.DroneDB)
            throw new BadRequestException("Cannot transfer a DroneDB file");

        var placement = new EntryPlacement(_fs, _logger);
        MovedSource? movedSource = null;

        try
        {
            _logger.LogInformation("Transferring {Kind} '{Source}' to '{Dest}'", isFolder ? "directory" : "file",
                sourcePath, destPath);
            placement.Place(destDdb, destPath, destEntry, sourceDdb.GetLocalPath(sourcePath), isFolder);

            // The copy is indexed with the source hashes, not hashed again
            placement.BeginIndexing();
            CopiedEntries.Index(sourceDdb, sourceEntry, destDdb, destPath, _fs);

            if (!destDdb.EntryExists(destPath))
                throw new DdbException($"Cannot find destination '{destPath}' after transfer, something wrong with ddb");

            // The builds of the transferred files (artifacts and pending markers, DDBCopyBuildArtifacts), so the
            // destination does not build them again; copied, never moved: a build may serve other entries with
            // the same content. A failure below leaves them as orphans for the recurring cleanup.
            var copiedBuilds = _artifacts.CopyBuilds(sourceDdb, files, destDdb);
            _logger.LogInformation("Copied {Count} build(s) of {Files} file(s) to the destination", copiedBuilds,
                files.Length);

            // Move semantics: the source index entries go last; once they are gone the destination is the only
            // copy the datasets list, so nothing after this point undoes the destination. Only what the copy holds
            // goes: a file uploaded into the source since the copy was made stays there
            if (!keepSource)
            {
                _logger.LogInformation("Removing source '{SourcePath}'", sourcePath);
                movedSource = new MovedSource(sourceDdb, sourcePath, isFolder, placement.Copied!, _fs, _logger);
                movedSource.RemoveFromIndex();
            }

            // The destination holds the entry and the source does not: the replaced entry is not needed any more
            placement.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer failed, attempting rollback");
            placement.Rollback();
            throw;
        }

        // The files of the moved source, which the index no longer lists: a failure is logged and leaves the
        // file for the reconciliation, never a rollback that would lose the files already deleted
        movedSource?.RemoveFiles();

        // After the commit: what the datasets tell their watchers is not a reason to undo the transfer
        if (!keepSource)
            await _changes.EntriesLeftAsync(sourceOrgSlug, sourceDsSlug, [sourcePath]);

        // The transferred files may be what pending builds of the destination waited for
        await _changes.EntriesArrivedAsync(destAccess, destOrgSlug, destDsSlug, [destPath]);

        _logger.LogInformation("Transfer OK");
    }

    public async Task<EntryDto> CopyAsync(string orgSlug, string dsSlug, string source, string dest,
        bool overwrite = false, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ddb = access.Ddb;

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

        // The checks below read the state of the dataset: one copy or transfer at a time in it
        using var lease = await _datasetLocks.AcquireAsync([LockKey(orgSlug, dsSlug)], ct);

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
        await _writePolicy.EnsureAllowedAsync(access.Dataset.Organization?.OwnerId, dest, isFolder, storageDelta);

        var placement = new EntryPlacement(_fs, _logger);
        Entry copiedEntry;

        try
        {
            _logger.LogInformation("Copying {Kind} '{Source}' to '{Dest}'", isFolder ? "directory" : "file", source,
                dest);
            placement.Place(ddb, dest, destEntry, ddb.GetLocalPath(source), isFolder);

            // The copy is indexed with the source hashes, not hashed again
            placement.BeginIndexing();
            CopiedEntries.Index(ddb, sourceEntry, ddb, dest, _fs);

            copiedEntry = ddb.GetEntry(dest)
                          ?? throw new DdbException($"Cannot find destination '{dest}' after copy, something wrong with ddb");

            // Commit: the previous destination is not needed any more (the new one is in place)
            placement.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy failed, attempting rollback");
            placement.Rollback();
            throw;
        }

        // Builds are named by content hash: the copy's content is the source's, its build serves both

        // After the commit, never undone by what follows: the copy may be what pending builds waited for (a
        // companion copied under its expected name)
        await _changes.EntriesArrivedAsync(access, orgSlug, dsSlug, [dest]);

        _logger.LogInformation("Copy OK");
        return copiedEntry.ToDto();
    }

    /// <summary>The key of a dataset in the lock of the transfers.</summary>
    private static string LockKey(string orgSlug, string dsSlug) =>
        (orgSlug + "/" + dsSlug).ToLowerInvariant();

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
}
