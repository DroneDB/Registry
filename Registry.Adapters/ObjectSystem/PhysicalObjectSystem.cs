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

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            IReadOnlyDictionary<string, string> copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task MakeBucketAsync(string bucketName, string location, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
        {
            return new Task<bool>(() => Directory.Exists(Path.Combine(_baseFolder, bucket)), cancellationToken);
        }

        public async Task RemoveBucketAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
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

        public Task<ListBucketsResult> EnumerateBucketsAsync(CancellationToken cancellationToken = default)
        {

            return new Task<ListBucketsResult>(() =>
            {
                var directories =  Directory.EnumerateDirectories(_baseFolder);

                return new ListBucketsResult
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
