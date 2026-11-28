using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The source of a move once its copy is in place: only what the copy holds leaves the source. An upload does not
/// wait for a transfer, so a file can arrive in a moved folder, or replace one of its files, after the copy was
/// made; that file is in neither the copy nor the destination, and it stays in the source with its index entry.
/// A source file belongs to the copy while it has the size and write time its copy has (<see cref="CopiedTree"/>).
/// </summary>
internal sealed class MovedSource(IDDB ddb, string path, bool isFolder, CopiedTree copied, IFileSystem fs,
    ILogger logger)
{
    private IReadOnlyList<string> _copiedFiles = [];

    /// <summary>
    /// Forgets in the source index what the copy holds: the entries of the copied files that still hold what was
    /// copied, the entries of files that are no longer on disk (nothing is lost with them), and the folders with
    /// nothing else left in them. An entry of a file that arrived or changed after the copy stays, and so do the
    /// folders above it. A source that is gone (another request moved or deleted it since the checks) is a
    /// <see cref="NotFoundException"/>: the transfer is undone and answers 404, not a 500.
    /// </summary>
    public void RemoveFromIndex()
    {
        // The files that are the copy's: the same set decides which index entries and which files go
        _copiedFiles = copied.Files.Keys.Where(HoldsCopy).ToArray();
        var copiedFiles = _copiedFiles.Select(DatasetPath).ToHashSet(PathComparison.Comparer);

        var rows = isFolder
            ? ddb.FindSummaries(EntryQuery.Under(path)).Where(row => row.Path != path).ToArray()
            : [];
        var kept = rows
            .Where(row => row.Type != EntryType.Directory && !copiedFiles.Contains(row.Path) &&
                          fs.Exists(ddb.GetLocalPath(row.Path)))
            .Select(row => row.Path)
            .ToHashSet(StringComparer.Ordinal);

        // A folder entry takes everything under it out of the index: only one without a kept entry below it goes
        bool Holds(string folder) => kept.Any(p => p.StartsWith(folder + "/", StringComparison.Ordinal));
        var removed = isFolder
            ? rows.Where(row => row.Type == EntryType.Directory ? !Holds(row.Path) : !kept.Contains(row.Path))
                .Select(row => row.Path)
                .ToList()
            : [];
        if (kept.Count == 0 && (isFolder || copiedFiles.Contains(path) || !fs.Exists(ddb.GetLocalPath(path))))
            removed.Add(path);

        string[] stays = removed.Contains(path) ? [] : kept.Count > 0 ? [.. kept] : [path];
        if (stays.Length > 0)
            logger.LogWarning(
                "'{SourcePath}' was moved, but {Count} of its file(s) arrived or changed in the source after it was copied: they stay in the source, with their index entries ({Sample})",
                path, stays.Length, Sample(stays));

        if (removed.Count == 0)
            return;

        try
        {
            ddb.Remove(removed);
        }
        catch (DdbException) when (ddb.GetEntry(path) is null)
        {
            throw new NotFoundException("Cannot find source entry: '" + path + "'");
        }
    }

    /// <summary>
    /// Deletes the files of the source that still hold what the copy holds, then the folders left empty, deepest
    /// first. What stays (a file that arrived or changed after the copy, one that cannot be deleted because it is
    /// locked or read-only) is logged: the destination is whole and indexed, and undoing it would lose the files
    /// already deleted here. The reconciliation sweep indexes a file that is left unindexed.
    /// </summary>
    public void RemoveFiles()
    {
        foreach (var relative in _copiedFiles.Where(HoldsCopy))
            BestEffort.Run(() => fs.RemoveEntry(LocalPath(relative), isFolder: false), ex => logger.LogWarning(ex,
                "Cannot delete '{Path}' of the moved source '{SourcePath}'", DatasetPath(relative), path));

        var root = ddb.GetLocalPath(path);
        if (!isFolder)
        {
            if (fs.Exists(root))
                LogLeft([path]);
            return;
        }

        foreach (var folder in copied.Folders.Append(string.Empty))
            BestEffort.Run(() => DeleteIfEmpty(LocalPath(folder)), ex => logger.LogWarning(ex,
                "Cannot delete the folder '{Path}' of the moved source '{SourcePath}'", DatasetPath(folder), path));

        BestEffort.Run(() =>
        {
            if (fs.FolderExists(root))
                LogLeft([.. fs.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(file => DatasetPath(CopiedTree.Relative(root, file)))]);
        }, ex => logger.LogWarning(ex, "Cannot list what stays in the moved source '{SourcePath}'", path));
    }

    private void LogLeft(IReadOnlyCollection<string> left)
    {
        if (left.Count > 0)
            logger.LogWarning(
                "The transfer of '{SourcePath}' is complete, but {Count} file(s) stay in its source folder: they arrived or changed after the copy, or could not be deleted ({Sample})",
                path, left.Count, Sample(left));
    }

    // Whether the source file at a copied path still holds what was copied (one that cannot be read does not)
    private bool HoldsCopy(string relative) => BestEffort.Get(() =>
    {
        var local = LocalPath(relative);
        return fs.Exists(local) && CopiedFile.Of(fs, local) == copied.Files[relative];
    }, _ => false);

    private void DeleteIfEmpty(string localFolder)
    {
        if (fs.FolderExists(localFolder) && !fs.EnumerateFileSystemEntries(localFolder).Any())
            fs.FolderDelete(localFolder, recursive: false);
    }

    private string DatasetPath(string relative) => relative.Length == 0 ? path : path + "/" + relative;

    private string LocalPath(string relative) => ddb.GetLocalPath(DatasetPath(relative));

    private static string Sample(IReadOnlyCollection<string> paths) =>
        string.Join(", ", paths.Take(10)) + (paths.Count > 10 ? ", ..." : string.Empty);
}
