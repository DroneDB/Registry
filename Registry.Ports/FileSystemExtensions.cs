using System;

namespace Registry.Ports;

/// <summary>
/// Extension methods for IFileSystem providing safe operations with error handling
/// </summary>
public static class FileSystemExtensions
{
    /// <summary>
    /// Deletes the file, or the folder with everything in it, at <paramref name="path"/> when it
    /// exists; never throws. The one best-effort delete of the Registry (RQ-10): scratch files,
    /// rolled-back writes, abandoned uploads and task folders. A failure goes to
    /// <paramref name="onError"/> (the caller logs it) and the caller carries on: what is left is
    /// removed by the abandoned-file sweeps.
    /// </summary>
    /// <returns>True when nothing is left at <paramref name="path"/> (or it is null or empty).</returns>
    public static bool TryDelete(this IFileSystem fs, string? path, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (string.IsNullOrEmpty(path))
            return true;

        try
        {
            if (fs.Exists(path))
                fs.Delete(path);
            else if (fs.FolderExists(path))
                fs.FolderDelete(path, true);
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return false;
        }
    }
}
