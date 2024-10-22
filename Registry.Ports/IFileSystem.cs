using System.Collections.Generic;
using System.Text;

namespace Registry.Ports;

public interface IFileSystem
{
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void WriteAllText(string path, string contents, Encoding encoding);
    void WriteAllBytes(string path, byte[] bytes);
    void AppendAllText(string path, string contents);
    void AppendAllText(string path, string contents, Encoding encoding);
    void AppendAllLines(string path, IEnumerable<string> contents);
    void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding);
    void Copy(string sourceFileName, string destFileName, bool overwrite = true);
    void FolderCopy(string sourceFileName, string destFileName, bool overwrite = true, bool recursive = true);
    void Move(string sourceFileName, string destFileName);
    void FolderMove(string sourceFileName, string destFileName);
    void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName);
    void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName, bool ignoreMetadataErrors);
    void Delete(string path);
    void FolderDelete(string path, bool recursive = true);
    bool Exists(string path);
    bool FolderExists(string path);
    void FolderCreate(string path);

}