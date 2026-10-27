using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface IFileSystem
{
    #region Synchronous File Operations

    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> and flushes them to the disk before it returns, so a
    /// file renamed over another one right after is whole after a crash. <paramref name="mode"/> decides what an existing
    /// file means (<see cref="FileMode.CreateNew"/> refuses it), <paramref name="share"/> what a reader may do meanwhile.
    /// </summary>
    void WriteAllBytesDurable(string path, byte[] bytes, FileMode mode = FileMode.Create, FileShare share = FileShare.None);

    /// <summary>The whole file as text (UTF-8, a BOM is honoured).</summary>
    string ReadAllText(string path);
    void Copy(string sourceFileName, string destFileName, bool overwrite = true);
    void Move(string sourceFileName, string destFileName);

    /// <summary>Moves a file, replacing <paramref name="destFileName"/> when <paramref name="overwrite"/> (a rename on one volume).</summary>
    void Move(string sourceFileName, string destFileName, bool overwrite);
    void Delete(string path);
    bool Exists(string path);

    #endregion

    #region Synchronous Folder Operations

    void FolderCopy(string sourceFolder, string destFolder, bool overwrite = true, bool recursive = true);
    void FolderMove(string sourceFolder, string destFolder);
    void FolderDelete(string path, bool recursive = true);
    bool FolderExists(string path);
    void FolderCreate(string path);

    #endregion

    #region Stream Operations

    Stream OpenRead(string path);
    Stream OpenWrite(string path, FileMode mode = FileMode.Create);

    #endregion

    #region File/Folder Information

    long GetFileSize(string path);
    DateTime GetLastWriteTime(string path);
    string[] GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    string[] GetDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    long GetDirectorySize(string folderPath);

    #endregion

    #region Path Operations

    /// <summary>
    /// Ensures the parent folder of the specified path exists
    /// </summary>
    void EnsureParentFolderExists(string path);

    #endregion

    #region Async File Operations

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);
    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);
    Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default);

    #endregion
}