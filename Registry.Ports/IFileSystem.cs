using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface IFileSystem
{
    #region Synchronous File Operations

    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllText(string path, string contents);
    void WriteAllText(string path, string contents, Encoding encoding);
    void WriteAllBytes(string path, byte[] bytes);
    void AppendAllText(string path, string contents);
    void AppendAllText(string path, string contents, Encoding encoding);
    void AppendAllLines(string path, IEnumerable<string> contents);
    void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding);
    void Copy(string sourceFileName, string destFileName, bool overwrite = true);
    void Move(string sourceFileName, string destFileName);
    void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName);
    void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName, bool ignoreMetadataErrors);
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
    Stream Open(string path, FileMode mode, FileAccess access, FileShare share);

    #endregion

    #region File/Folder Information

    long GetFileSize(string path);
    DateTime GetCreationTime(string path);
    DateTime GetLastWriteTime(string path);
    DateTime GetLastAccessTime(string path);
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
    Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default);
    Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default);
    Task AppendAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);
    Task AppendAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default);

    #endregion
}