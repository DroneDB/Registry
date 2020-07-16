using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Ports.ObjectSystem
{
    /// <summary>
    /// Object system abstraction
    /// </summary>
    public interface IObjectSystem
    {

        Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default);

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
        Task<EnumerateBucketsResult> EnumerateBucketsAsync(CancellationToken cancellationToken = default);
    }
}
