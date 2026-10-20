namespace Registry.Ports;

/// <summary>
/// Extension methods for IFileSystem providing safe operations with error handling
/// </summary>
public static class FileSystemExtensions
{
    #region Safe File Operations

    /// <summary>
    /// Safely deletes a file, returning true if successful, false otherwise
    /// </summary>
    public static bool SafeDelete(this IFileSystem fs, string path)
    {
        try
        {
            fs.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely deletes a folder, returning true if successful, false otherwise
    /// </summary>
    public static bool SafeFolderDelete(this IFileSystem fs, string path, bool recursive = true)
    {
        try
        {
            fs.FolderDelete(path, recursive);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
