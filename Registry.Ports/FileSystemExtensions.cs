using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// Safely copies a file, returning true if successful, false otherwise
    /// </summary>
    public static bool SafeCopy(this IFileSystem fs, string source, string dest, bool overwrite = true)
    {
        try
        {
            fs.Copy(source, dest, overwrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely gets file creation time, returning null on failure
    /// </summary>
    public static DateTime? SafeGetCreationTime(this IFileSystem fs, string path)
    {
        try
        {
            return fs.GetCreationTime(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely gets file size, returning null on failure
    /// </summary>
    public static long? SafeGetFileSize(this IFileSystem fs, string path)
    {
        try
        {
            return fs.GetFileSize(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely gets last write time, returning null on failure
    /// </summary>
    public static DateTime? SafeGetLastWriteTime(this IFileSystem fs, string path)
    {
        try
        {
            return fs.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Safe Folder Operations

    /// <summary>
    /// Recursively removes empty folders
    /// </summary>
    public static void RemoveEmptyFolders(this IFileSystem fs, string folder, bool removeSelf = false)
    {
        try
        {
            if (!fs.FolderExists(folder)) return;

            // Recursive call
            foreach (var f in fs.EnumerateDirectories(folder))
            {
                fs.RemoveEmptyFolders(f, true);
            }

            // If not empty we don't have to delete it
            if (fs.EnumerateFileSystemEntries(folder).Any()) return;

            if (!removeSelf) return;

            try
            {
                fs.FolderDelete(folder, false);
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore
            }
            catch (DirectoryNotFoundException)
            {
                // Ignore
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
        }
    }

    /// <summary>
    /// Attempts to delete files and folders with multiple retries
    /// </summary>
    public static string[] SafeTreeDelete(this IFileSystem fs, string baseTempFolder, int rounds = 3, int delay = 500)
    {
        var entries = new List<string>(
            fs.EnumerateFileSystemEntries(baseTempFolder, "*", SearchOption.AllDirectories));

        for (var n = 0; n < rounds; n++)
        {
            foreach (var entry in entries.ToArray())
            {
                try
                {
                    if (fs.FolderExists(entry))
                    {
                        fs.FolderDelete(entry, true);
                        entries.Remove(entry);
                        continue;
                    }

                    if (fs.Exists(entry))
                    {
                        fs.Delete(entry);
                        entries.Remove(entry);
                        continue;
                    }

                    entries.Remove(entry);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Exception: " + ex.Message);
                }
            }

            if (entries.Count == 0)
                return [];

            Thread.Sleep(delay);
        }

        return entries.ToArray();
    }

    #endregion

    #region File Access with Retry

    /// <summary>
    /// Waits for a file to become accessible with retries
    /// </summary>
    public static async Task<Stream?> WaitForFileAsync(this IFileSystem fs, string fullPath, FileMode mode, FileAccess access,
        FileShare share, int delay = 50, int retries = 1200)
    {
        for (var numTries = 0; numTries < retries; numTries++)
        {
            Stream? stream = null;
            try
            {
                stream = fs.Open(fullPath, mode, access, share);
                return stream;
            }
            catch (IOException)
            {
                if (stream != null)
                {
                    await stream.DisposeAsync();
                }

                await Task.Delay(delay);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a file is accessible (not locked by another process)
    /// </summary>
    public static bool IsFileAccessible(this IFileSystem fs, string path)
    {
        try
        {
            if (!fs.Exists(path)) return false;
            using var stream = fs.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Folder Copy with Exclusions

    /// <summary>
    /// Copies a folder with optional exclusions
    /// </summary>
    public static void FolderCopyWithExclusions(this IFileSystem fs, string sourceDirectory, string targetDirectory,
        bool overwrite = false, string[]? excludes = null)
    {
        fs.FolderCreate(targetDirectory);

        // Copy each file into the new directory
        foreach (var file in fs.GetFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(file);
            if (excludes != null && excludes.Contains(fileName)) continue;

            var dest = Path.Combine(targetDirectory, fileName);
            if (fs.Exists(dest) && !overwrite) continue;

            fs.Copy(file, dest, overwrite);
        }

        // Copy each subdirectory using recursion
        foreach (var dir in fs.GetDirectories(sourceDirectory))
        {
            var dirName = Path.GetFileName(dir);
            var nextTargetSubDir = Path.Combine(targetDirectory, dirName);

            fs.FolderCopyWithExclusions(dir, nextTargetSubDir, overwrite, excludes);
        }
    }

    /// <summary>
    /// Moves a folder with optional exclusions (copy + delete)
    /// </summary>
    public static void FolderMoveWithExclusions(this IFileSystem fs, string sourceDirectory, string targetDirectory,
        string[]? excludes = null)
    {
        fs.FolderCopyWithExclusions(sourceDirectory, targetDirectory, true, excludes);
        fs.FolderDelete(sourceDirectory, true);
    }

    #endregion
}
