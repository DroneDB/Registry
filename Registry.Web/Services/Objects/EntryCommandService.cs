using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>Default <see cref="IEntryCommandService"/>.</summary>
public sealed class EntryCommandService : IEntryCommandService
{
    private readonly IDatasetAccessResolver _access;
    private readonly DatasetPathPolicy _paths;
    private readonly IFileSystem _fs;
    private readonly EntryChangeNotifier _changes;
    private readonly EntryJobs _jobs;
    private readonly ILogger<EntryCommandService> _logger;

    public EntryCommandService(IDatasetAccessResolver access, DatasetPathPolicy paths, IFileSystem fs,
        EntryChangeNotifier changes, EntryJobs jobs, ILogger<EntryCommandService> logger)
    {
        _access = access;
        _paths = paths;
        _fs = fs;
        _changes = changes;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<EntryDto> CreateFolderAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct)).Ddb;

        // libddb's path rule: no traversal, no absolute path, not reserved
        var folder = _paths.Normalize(path);

        // An entry of any type with that name (letter case ignored, D-040): the one "already exists" answer, before
        // anything is written. A name in a folder that differs in case from the indexed one is created in that folder
        var location = DatasetEntryLookup.Locate(ddb, folder);
        if (location.Existing is { } existing)
            throw new NameClashException(existing.Path);
        folder = location.Path;

        _logger.LogInformation("Adding folder '{Path}' to '{OrgSlug}/{DsSlug}'", folder, orgSlug, dsSlug);
        ddb.Add(folder);

        // The entry as indexed, with its real mtime (FR-DEV-13); now when the index cannot give it back. The folder
        // exists by now: a failed read must not turn the creation into an error (a retry would be a 409). Any
        // failure counts (Ddb wraps only a plain DdbException; its subclasses and others pass through), except
        // cancellation
        Entry? indexed = null;
        try
        {
            indexed = ddb.GetEntry(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cannot read back folder '{Path}' of '{OrgSlug}/{DsSlug}'", folder, orgSlug, dsSlug);
        }

        return indexed.ToDto() ??
               new EntryDto { Path = folder, Type = EntryType.Directory, Size = 0, ModifiedTime = WholeSecondsNow() };
    }

    // mtime is whole Unix seconds, like the index's
    private static DateTime WholeSecondsNow() =>
        DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public async Task<EntryDto> MoveAsync(string orgSlug, string dsSlug, string? sourcePath, string? destPath,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ddb = access.Ddb;

        var source = _paths.Normalize(sourcePath, "source path");
        var dest = _paths.Normalize(destPath, "destination path");

        var sourceEntry = ddb.GetEntry(source) ?? throw new NotFoundException("Cannot find source entry: '" + source + "'");

        // Where the destination is (D-040, as for a new folder): its folders in the letter case of the existing ones
        // ("FOLDER/a.txt" with the folder "Folder" is "Folder/a.txt", no sibling folder "FOLDER" appears on a
        // case-sensitive file system), its own name as the request writes it (a rename may change only the case)
        var location = DatasetEntryLookup.Locate(ddb, dest);
        dest = WithName(location.Path, dest);

        // Names compare without regard to case: "folder/x" is inside the folder "Folder"; the entry itself under
        // another case ("a.tif" to "A.tif") is a rename
        if (string.Equals(dest, source, StringComparison.Ordinal) ||
            dest.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("Cannot move a path onto itself or one of its descendants");

        var isFolder = sourceEntry.Type == EntryType.Directory;

        if (sourceEntry.Type == EntryType.DroneDB)
            throw new BadRequestException("Cannot move a DroneDB file");

        // An entry of any type that holds the destination name (letter case ignored, D-040) is never replaced: 409 with
        // the one "already exists" message, before the file system is touched. The entry itself does not count, so
        // a rename that only changes the letter case ("a.tif" to "A.tif") goes through
        if (location.Existing is { } existing &&
            !string.Equals(existing.Path, source, StringComparison.Ordinal))
            throw new NameClashException(existing.Path);

        var destLocalPath = ddb.GetLocalPath(dest);
        _logger.LogInformation("Moving {Kind} '{Source}' to '{Dest}'", isFolder ? "directory" : "file", source, dest);
        _fs.EnsureParentFolderExists(destLocalPath);
        _fs.MoveEntry(ddb.GetLocalPath(source), destLocalPath, isFolder);

        ddb.Move(source, dest);

        var updatedEntry = ddb.GetEntry(dest) ?? throw new DdbException($"Cannot find destination '{dest}' after move");

        // A file with another extension may be another type: built again when it is buildable
        if (!string.Equals(Path.GetExtension(source), Path.GetExtension(dest), StringComparison.OrdinalIgnoreCase) &&
            await _jobs.RebuildIfBuildableAsync(access, orgSlug, dsSlug, updatedEntry))
            _logger.LogInformation("Extension changed and file is buildable, triggered a build of '{Path}'", dest);

        // The entry left its old path; a companion renamed to the name its main file expects unblocks the build that
        // waited for it
        await _changes.EntriesLeftAsync(orgSlug, dsSlug, [source]);
        await _changes.EntriesArrivedAsync(access, orgSlug, dsSlug, [dest]);

        return updatedEntry.ToDto();
    }

    // The folder of `located` followed by the last segment of `requested`
    private static string WithName(string located, string requested)
    {
        var folderEnd = located.LastIndexOf('/');
        var nameStart = requested.LastIndexOf('/') + 1;
        return (folderEnd < 0 ? string.Empty : located[..(folderEnd + 1)]) + requested[nameStart..];
    }

    public async Task DeleteAsync(string orgSlug, string dsSlug, string[] paths, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ddb = access.Ddb;

        _logger.LogInformation("Deleting {PathCount} path(s) from '{OrgSlug}/{DsSlug}'", paths.Length, orgSlug, dsSlug);

        // libddb's path rule (a reserved path is refused like any other invalid path)
        paths = [.. paths.Select(p => _paths.Normalize(p))];

        foreach (var path in paths)
            if (!ddb.EntryExists(path))
                throw new BadRequestException($"Path '{path}' not found in dataset");

        // A failure of the index keeps its own type (busy: 503, other: 500)
        foreach (var path in paths)
            RemoveEntry(ddb, path);

        await _changes.EntriesLeftAsync(orgSlug, dsSlug, paths);
        await _jobs.RebuildAfterSidecarRemovalAsync(access, orgSlug, dsSlug, paths);
    }

    public async Task<DeleteBatchResponse> DeleteBatchAsync(string orgSlug, string dsSlug, string[] paths,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ddb = access.Ddb;

        _logger.LogInformation("Deleting a batch of {Count} path(s) from '{OrgSlug}/{DsSlug}'", paths.Length,
            orgSlug, dsSlug);

        var deleted = new List<string>();
        var failed = new Dictionary<string, string>();

        foreach (var requested in paths)
        {
            var check = _paths.Check(requested);
            if (check.Path == null)
            {
                failed[requested] = check.Rejected == EntryPathRejection.Reserved
                    ? "Path is reserved and cannot be deleted"
                    : $"Invalid path ({check.Rejected})";
                continue;
            }

            var path = check.Path;
            try
            {
                if (!ddb.EntryExists(path))
                {
                    failed[requested] = "Path not found in dataset";
                    continue;
                }

                RemoveEntry(ddb, path);
                deleted.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete {ObjPath}: {Reason}", path, ex.Message);
                failed[requested] = ex.Message;
            }
        }

        if (deleted.Count > 0)
            await _changes.EntriesLeftAsync(orgSlug, dsSlug, deleted);

        await _jobs.RebuildAfterSidecarRemovalAsync(access, orgSlug, dsSlug, deleted);

        _logger.LogInformation("DeleteBatch complete: {DeletedCount} deleted, {FailedCount} failed",
            deleted.Count, failed.Count);

        return new DeleteBatchResponse { Deleted = [.. deleted], Failed = failed };
    }

    /// <summary>
    /// Removes <paramref name="path"/> from the index first, then its files: a missing or undeletable file is
    /// not critical, it is logged and a later cleanup removes it.
    /// </summary>
    private void RemoveEntry(IDDB ddb, string path)
    {
        var files = ddb.FindSummaries(EntryQuery.Under(path)).Where(item => item.Type != EntryType.Directory).ToArray();

        _logger.LogInformation("Removing from DDB: {ObjPath}", path);
        ddb.Remove(path);

        _logger.LogInformation("Deleting {FilesCount} files for path {ObjPath}", files.Length, path);
        foreach (var file in files)
        {
            var local = ddb.GetLocalPath(file.Path);
            try
            {
                if (!_fs.RemoveEntry(local, isFolder: false))
                    _logger.LogWarning("Cannot delete local file {ObjPath}: {Reason}", local,
                        $"Cannot find local file '{local}' for object '{file.Path}'");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Cannot delete local file {ObjPath}: {Reason}", local, ex.Message);
            }
        }
    }
}
