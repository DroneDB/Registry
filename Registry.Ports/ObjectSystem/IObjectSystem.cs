using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Registry.Ports.ObjectSystem
{
    /// <summary>
    /// Objectsystem abstraction
    /// </summary>
    public interface IObjectSystem
    {
        IEnumerable<string> EnumerateBuckets(string searchPattern, SearchOption searchOption);
        void CreateDirectory(string bucket, string path);
        void DeleteObject(string bucket, string path);
        bool DirectoryExists(string bucket, string path);
        IEnumerable<string> EnumerateObjects(string bucket, string path, string searchPattern, SearchOption searchOption);
        IEnumerable<string> EnumerateFolders(string bucket, string path, string searchPattern, SearchOption searchOption);
        bool ObjectExists(string bucket, string path);
        string ReadAllText(string bucket, string path, Encoding encoding);
        byte[] ReadAllBytes(string bucket, string path);
        void RemoveDirectory(string bucket, string path, bool recursive);
        void WriteAllText(string bucket, string path, string contents, Encoding encoding);
        void WriteAllBytes(string bucket, string path, byte[] contents);
    }
}
