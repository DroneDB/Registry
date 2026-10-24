#nullable enable
using Registry.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The local files of a dataset entry, a folder (with its tree) or a file: the one copy of the
/// "folder or file" choice the entry commands and their rollbacks make.
/// </summary>
internal static class EntryFiles
{
    /// <summary>Copies the entry at <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static void CopyEntry(this IFileSystem fs, string source, string destination, bool isFolder)
    {
        if (isFolder)
            fs.FolderCopy(source, destination);
        else
            fs.Copy(source, destination);
    }

    /// <summary>Moves the entry at <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static void MoveEntry(this IFileSystem fs, string source, string destination, bool isFolder)
    {
        if (isFolder)
            fs.FolderMove(source, destination);
        else
            fs.Move(source, destination);
    }

    /// <summary>Deletes the entry at <paramref name="path"/>, when it exists.</summary>
    /// <returns>Whether there was something to delete.</returns>
    public static bool RemoveEntry(this IFileSystem fs, string path, bool isFolder)
    {
        if (isFolder)
        {
            if (!fs.FolderExists(path))
                return false;
            fs.FolderDelete(path);
            return true;
        }

        if (!fs.Exists(path))
            return false;
        fs.Delete(path);
        return true;
    }
}
