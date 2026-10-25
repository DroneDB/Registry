using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;

namespace Registry.Adapters;

/// <summary>
/// IFileSystem implementation wrapping System.IO for testability.
/// </summary>
public class FileSystem : IFileSystem
{
    #region Synchronous File Operations

    public void WriteAllBytes(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
    }

    public void Copy(string sourceFileName, string destFileName, bool overwrite = true)
    {
        File.Copy(sourceFileName, destFileName, overwrite);
    }

    public void Move(string sourceFileName, string destFileName) => Move(sourceFileName, destFileName, false);

    public void Move(string sourceFileName, string destFileName, bool overwrite)
    {
        try
        {
            File.Move(sourceFileName, destFileName, overwrite);
        }
        catch (IOException ex) when (ex.Message.Contains("cross-device") || ex.Message.Contains("Invalid cross-device link"))
        {
            // Cross-device move not supported on Linux, fall back to copy + delete
            File.Copy(sourceFileName, destFileName, overwrite);
            File.Delete(sourceFileName);
        }
    }

    public void Delete(string path)
    {
        File.Delete(path);
    }

    public bool Exists(string path)
    {
        return File.Exists(path);
    }

    #endregion

    #region Synchronous Folder Operations

    public void FolderCopy(string sourceFolder, string destFolder, bool overwrite = true, bool recursive = true)
    {
        Directory.CreateDirectory(destFolder);
        foreach (var file in Directory.GetFiles(sourceFolder))
        {
            File.Copy(file, Path.Combine(destFolder, Path.GetFileName(file)), overwrite);
        }

        if (recursive)
        {
            foreach (var dir in Directory.GetDirectories(sourceFolder))
            {
                FolderCopy(dir, Path.Combine(destFolder, Path.GetFileName(dir)), overwrite, recursive);
            }
        }
    }

    public void FolderMove(string sourceFolder, string destFolder)
    {
        try
        {
            Directory.Move(sourceFolder, destFolder);
        }
        catch (IOException ex) when (ex.Message.Contains("cross-device") || ex.Message.Contains("Invalid cross-device link"))
        {
            // Cross-device move not supported on Linux, fall back to copy + delete
            FolderCopy(sourceFolder, destFolder, true, true);
            Directory.Delete(sourceFolder, true);
        }
    }

    public void FolderDelete(string path, bool recursive = true)
    {
        Directory.Delete(path, recursive);
    }

    public bool FolderExists(string path)
    {
        return Directory.Exists(path);
    }

    public void FolderCreate(string path)
    {
        Directory.CreateDirectory(path);
    }

    #endregion

    #region Stream Operations

    public Stream OpenRead(string path)
    {
        return File.OpenRead(path);
    }

    public Stream OpenWrite(string path, FileMode mode = FileMode.Create)
    {
        // Large (1 MiB) buffer + async + sequential-scan hints for high-throughput large-file writes.
        // FileShare.Read preserves the previous sharing semantics of new FileStream(path, mode, Write).
        return new FileStream(path, mode, FileAccess.Write, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    #endregion

    #region File/Folder Information

    public long GetFileSize(string path)
    {
        return new FileInfo(path).Length;
    }

    public DateTime GetLastWriteTime(string path)
    {
        return File.GetLastWriteTime(path);
    }

    public string[] GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.GetFiles(path, searchPattern, searchOption);
    }

    public string[] GetDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.GetDirectories(path, searchPattern, searchOption);
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.EnumerateFiles(path, searchPattern, searchOption);
    }

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.EnumerateDirectories(path, searchPattern, searchOption);
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.EnumerateFileSystemEntries(path, searchPattern, searchOption);
    }

    public long GetDirectorySize(string folderPath)
    {
        var di = new DirectoryInfo(folderPath);
        return di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
    }

    #endregion

    #region Path Operations

    public void EnsureParentFolderExists(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder != null)
            Directory.CreateDirectory(folder);
    }

    #endregion

    #region Async File Operations

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        return File.ReadAllBytesAsync(path, cancellationToken);
    }

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        return File.WriteAllTextAsync(path, contents, cancellationToken);
    }

    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        return File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    #endregion
}