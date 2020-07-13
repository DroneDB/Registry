using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Registry.Ports.FileSystem
{
    /// <summary>
    /// Filesystem abstraction
    /// </summary>
    public interface IFileSystem
    {
        public void CreateDirectory(string path);
        public void DeleteFile(string path);
        public bool DirectoryExists(string path);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
        public IEnumerable<string> EnumerateFolders(string path, string searchPattern, SearchOption searchOption);
        public bool FileExists(string path);
        public string ReadAllText(string path, Encoding encoding);
        public byte[] ReadAllBytes(string path);
        public void RemoveDirectory(string path, bool recursive);
        public void WriteAllText(string path, string contents, Encoding encoding);
        public void WriteAllBytes(string path, byte[] contents);
    }
}
