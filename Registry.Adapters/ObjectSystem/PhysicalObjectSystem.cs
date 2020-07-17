using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.FileSystem.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    /// <summary>
    /// Physical implementation of filesystem interface
    /// </summary>
    public class PhysicalObjectSystem : IObjectSystem
    {
        private readonly string _baseFolder;

        public PhysicalObjectSystem(string baseFolder)
        {
            _baseFolder = baseFolder;
        }

        public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
        {
            return new Task<bool>(() => Directory.Exists(Path.Combine(_baseFolder, bucket)), cancellationToken);
        }

        public void CreateDirectory(string bucket, string path)
        {
            CheckPath(path);

            Directory.CreateDirectory(Path.Combine(_baseFolder, bucket, path));
        }

        private void CheckPath(string path)
        {
            if (path.Contains(".."))
                throw new InvalidOperationException("Parent path separator is not supported");
        }

        public Task DeleteObject(string bucket, string path, CancellationToken cancellationToken) 
        {

            return new Task(() => {
                CheckPath(path);

                File.Delete(Path.Combine(_baseFolder, bucket, path));
            
            });
               

            
        }

        public bool DirectoryExists(string bucket, string path)
        {
            return Directory.Exists(Path.Combine(_baseFolder, bucket, path));
        }

        public IEnumerable<string> EnumerateObjects(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateFiles(Path.Combine(_baseFolder, bucket, path), searchPattern, searchOption);
        }

        public IEnumerable<string> EnumerateFolders(string bucket, string path, string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateDirectories(Path.Combine(_baseFolder, bucket, path), searchPattern, searchOption);
        }

        public string ReadAllText(string bucket, string path, Encoding encoding)
        {
            return File.ReadAllText(Path.Combine(_baseFolder, bucket, path), encoding);
        }

        public byte[] ReadAllBytes(string bucket, string path)
        {
            return File.ReadAllBytes(Path.Combine(_baseFolder, bucket, path));
        }

        public void RemoveDirectory(string bucket, string path, bool recursive)
        {
            Directory.Delete(Path.Combine(_baseFolder, bucket, path), recursive);
        }

        public void WriteAllText(string bucket, string path, string contents, Encoding encoding)
        {
            File.WriteAllText(Path.Combine(_baseFolder, bucket, path), contents, encoding);
        }

        public void WriteAllBytes(string bucket, string path, byte[] contents)
        {
            File.WriteAllBytes(Path.Combine(_baseFolder, bucket, path), contents);
        }

        public Task<EnumerateBucketsResult> EnumerateBucketsAsync(CancellationToken cancellationToken = default)
        {

            return new Task<EnumerateBucketsResult>(() =>
            {
                var directories =  Directory.EnumerateDirectories(_baseFolder);

                return new EnumerateBucketsResult
                {
                    // NOTE: We could create a .owner file that contains the owner of the folder to "simulate" the S3 filesystem
                    // Better: We can create a .info file that contains the metadata and the owner of the folder
                    Owner = "",
                    Buckets = directories.Select(dir => new BucketInfo
                    {
                        Name = Path.GetDirectoryName(dir),
                        CreationDate = Directory.GetCreationTime(dir)
                    })
                };

            }, cancellationToken);
            

        }

        public Task<bool> ObjectExists(string bucket, string path, CancellationToken cancellationToken = default)
        {
            return new Task<bool>(() => {
                return File.Exists(Path.Combine(_baseFolder, bucket, path));
            }, cancellationToken);
        }
    }
}
