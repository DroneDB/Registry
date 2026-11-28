using System;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The placing of a copied entry in a dataset, the one way a copy and a transfer write their destination: the
/// bytes are copied to a temporary sibling and promoted by a rename, so the destination is never half written and
/// never replaced in place; an entry that is replaced goes to a trash sibling first and comes back when the
/// operation fails. A rollback undoes only what this placement did: it never removes an entry it did not create.
/// </summary>
internal sealed class EntryPlacement(IFileSystem fs, ILogger logger)
{
    private IDDB? _ddb;
    private string _path = string.Empty;
    private string _localPath = string.Empty;
    private bool _isFolder;
    private bool _replacedWasFolder;
    private string? _tempPath;
    private string? _trashPath;
    private bool _tempWritten;
    private bool _promoted;
    private bool _replaced;
    private bool _indexed;

    /// <summary>
    /// Copies <paramref name="sourceLocalPath"/> to <paramref name="destPath"/> of <paramref name="destDdb"/> and
    /// promotes it, after moving <paramref name="replaced"/> (the entry the destination holds, when the request
    /// replaces one) to the trash and forgetting it in the index. Index the copy next (after
    /// <see cref="BeginIndexing"/>), then <see cref="Commit"/> or <see cref="Rollback"/>.
    /// </summary>
    /// <exception cref="System.IO.IOException">The destination appeared meanwhile: nothing of it is replaced.</exception>
    public void Place(IDDB destDdb, string destPath, Entry? replaced, string sourceLocalPath, bool isFolder)
    {
        _ddb = destDdb;
        _path = destPath;
        _localPath = destDdb.GetLocalPath(destPath);
        _isFolder = isFolder;
        _replacedWasFolder = replaced?.Type == EntryType.Directory;

        fs.EnsureParentFolderExists(_localPath);

        var token = Guid.NewGuid().ToString("N");
        _tempPath = _localPath + ".copying-" + token;

        logger.LogInformation("Copying '{Source}' -> '{Temp}'", sourceLocalPath, _tempPath);
        _tempWritten = true;
        fs.CopyEntry(sourceLocalPath, _tempPath, isFolder);

        if (replaced != null)
        {
            // The destination goes to the trash BEFORE its index entry is removed: when the move fails the index
            // still matches the disk and the rollback has nothing extra to undo
            _trashPath = _localPath + ".trash-" + token;
            logger.LogInformation("Moving the existing '{Dest}' -> trash '{Trash}'", destPath, _trashPath);
            if (_replacedWasFolder ? fs.FolderExists(_localPath) : fs.Exists(_localPath))
                fs.MoveEntry(_localPath, _trashPath, _replacedWasFolder);
            _replaced = true;

            destDdb.Remove(destPath);
        }

        logger.LogInformation("Promoting temp '{Temp}' -> '{Dest}'", _tempPath, destPath);
        fs.MoveEntry(_tempPath, _localPath, isFolder);
        _tempWritten = false;
        _promoted = true;
    }

    /// <summary>
    /// The promoted copy is about to be indexed: from now on a rollback forgets whatever of it the index holds, also
    /// when the indexing itself fails half way (a folder whose files are listed and whose last add throws).
    /// </summary>
    public void BeginIndexing() => _indexed = true;

    /// <summary>The previous destination is not needed any more (the new one is in place): its trash is deleted.</summary>
    public void Commit()
    {
        if (_replaced)
            Undo("trash copy", () => fs.RemoveEntry(_trashPath!, _replacedWasFolder), LogLevel.Warning);
    }

    /// <summary>Undoes the placement in reverse order: the index entry, the promoted copy, the temporary copy, then the replaced entry comes back.</summary>
    public void Rollback()
    {
        if (_ddb == null)
            return;

        // Only what the index holds: the indexing may have failed before it listed anything
        if (_indexed)
            Undo("DDB add", () =>
            {
                if (_ddb.EntryExists(_path))
                    _ddb.Remove(_path);
            });

        // A partially promoted destination goes before the trash is put back
        if (_promoted)
            Undo("file system copy", () => fs.RemoveEntry(_localPath, _isFolder));

        // The temporary copy, when it was never promoted
        if (_tempWritten)
            Undo("temp copy", () => fs.RemoveEntry(_tempPath!, _isFolder));

        // The original destination, when it was moved aside
        if (_replaced)
            RestoreFromTrash();
    }

    private void RestoreFromTrash()
    {
        try
        {
            logger.LogWarning("Restoring original destination '{Dest}' from trash '{Trash}'", _path, _trashPath);
            if (_replacedWasFolder ? fs.FolderExists(_trashPath!) : fs.Exists(_trashPath!))
                fs.MoveEntry(_trashPath!, _localPath, _replacedWasFolder);

            // Re-index the restored entry so the DDB stays consistent
            Undo("re-index of the restored destination", () => _ddb!.AddRaw(_path));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore original destination '{Dest}' from trash '{Trash}'", _path, _trashPath);
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
            logger.Log(failureLevel, ex, "Failed to roll back the {Step}", step);
        }
    }
}
