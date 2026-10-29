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

        if (ddb.EntryExists(folder))
            throw new ConflictException("Cannot create a folder on another entry");

        _logger.LogInformation("Adding folder '{Path}' to '{OrgSlug}/{DsSlug}'", folder, orgSlug, dsSlug);
        ddb.Add(folder);

        return new EntryDto { Path = folder, Type = EntryType.Directory, Size = 0 };
    }

    public async Task<EntryDto> MoveAsync(string orgSlug, string dsSlug, string? sourcePath, string? destPath,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ddb = access.Ddb;

        var source = _paths.Normalize(sourcePath, "source path");
        var dest = _paths.Normalize(destPath, "destination path");

        var sourceEntry = ddb.GetEntry(source) ?? throw new NotFoundException("Cannot find source entry: '" + source + "'");

        if ((dest + "/").StartsWith(source + "/", StringComparison.Ordinal))
            throw new BadRequestException("Cannot move a path onto itself or one of its descendants");

        var isFolder = sourceEntry.Type == EntryType.Directory;
        if (ddb.GetEntry(dest) is { } destEntry && isFolder != (destEntry.Type == EntryType.Directory))
            throw new BadRequestException(isFolder ? "Cannot move a folder on a file" : "Cannot move a file on a folder");

        if (sourceEntry.Type == EntryType.DroneDB)
            throw new BadRequestException("Cannot move a DroneDB file");

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

        // A companion renamed to the name its main file expects unblocks the build that waited for it
        await _changes.EntriesArrivedAsync(access, orgSlug, dsSlug, [source, dest]);

        return updatedEntry.ToDto();
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

        await _changes.EntriesChangedAsync(orgSlug, dsSlug, paths);
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
            await _changes.EntriesChangedAsync(orgSlug, dsSlug, deleted);

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
