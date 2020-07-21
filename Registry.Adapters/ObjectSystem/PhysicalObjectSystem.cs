using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeMapping;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    /// <summary>
    /// Physical implementation of filesystem interface
    /// </summary>
    public class PhysicalObjectSystem : IObjectSystem
    {
        public bool UseStrictNamingConvention { get; }
        private readonly string _baseFolder;
        public const string InfoFolder = ".info";
        public const string PolicySuffix = "policy";

        private readonly string _infoFolderPath;

        public PhysicalObjectSystem(string baseFolder, bool useStrictNamingConvention = false)
        {
            // We are not there yet
            if (useStrictNamingConvention)
                throw new NotImplementedException("UseStrictNamingConvention is not implemented yet");

            UseStrictNamingConvention = useStrictNamingConvention;
            _baseFolder = baseFolder;

            if (!Directory.Exists(_baseFolder))
                throw new ArgumentException($"'{_baseFolder}' does not exists");

            _infoFolderPath = Path.Combine(_baseFolder, InfoFolder);

            // Let's ensure that the info folder exists
            if (!Directory.Exists(_infoFolderPath))
                Directory.CreateDirectory(_infoFolderPath);

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
            await EnsureBucketExistsAsync(bucketName, cancellationToken);

            var objectPath = GetObjectPath(bucketName, objectName);
            EnsurePathExists(objectPath);

            var bucketInfo = GetBucketInfo(bucketName) ?? UpdateBucketInfo(bucketName);

            var objectInfo = bucketInfo.Objects.FirstOrDefault(item => item.Name == objectName) ?? 
                             UpdateObjectInfo(bucketName, objectName);


            return new ObjectInfo(
                objectInfo.Name,
                objectInfo.Size,
                objectInfo.LastModified,
                objectInfo.ETag,
                objectInfo.ContentType,
                objectInfo.MetaData);

        }

        /// <summary>
        /// Updates bucket info cache
        /// </summary>
        /// <param name="bucketName"></param>
        /// <returns></returns>
        private BucketInfoDto UpdateBucketInfo(string bucketName)
        {
            var bucketInfo = GenerateBucketInfo(bucketName);

            var bucketInfoPath = GetBucketInfoPath(bucketName);

            File.WriteAllText(bucketInfoPath, JsonConvert.SerializeObject(bucketInfo, Formatting.Indented));

            return bucketInfo;
        }

        /// <summary>
        /// Updates object info
        /// </summary>
        /// <param name="bucketName"></param>
        /// <param name="objectName"></param>
        /// <returns></returns>
        private ObjectInfoDto UpdateObjectInfo(string bucketName, string objectName)
        {
            var objectInfo = GenerateObjectInfo(bucketName, objectName);

            var bucketInfo = GetBucketInfo(bucketName);
            var bucketInfoPath = GetBucketInfoPath(bucketName);

            bucketInfo.Objects = bucketInfo.Objects.Where(item => item.Name != objectName).Concat(new[] { objectInfo }).ToArray();

            File.WriteAllText(bucketInfoPath,JsonConvert.SerializeObject(bucketInfo, Formatting.Indented));

            return objectInfo;

        }

        private string GetObjectPath(string bucketName, string objectName)
        {
            return Path.Combine(_baseFolder, bucketName, objectName);
        }

        private ObjectInfoDto GenerateObjectInfo(string bucketName, string objectName)
        {
            var objectPath = GetObjectPath(bucketName, objectName);

            var fileInfo = new FileInfo(objectPath);

            var objectInfo = new ObjectInfoDto
            {
                ContentType = MimeUtility.GetMimeMapping(objectPath),
                ETag = CalculateETag(objectPath, fileInfo),
                LastModified = File.GetLastWriteTime(objectPath),
                Size = fileInfo.Length,
                Name = objectName,
                MetaData = new Dictionary<string, string>()
            };

            return objectInfo;

        }

        private string CalculateETag(string filePath, FileInfo info)
        {
            // 5GB
            var chunkSize = 5L * 1024 * 1024 * 1024;

            var parts = (int)Math.Ceiling((double)info.Length / chunkSize);

            return AdaptersUtils.CalculateMultipartEtag(File.ReadAllBytes(filePath), parts);

        }

        private string CalculateETag(string filePath)
        {
            return CalculateETag(filePath, new FileInfo(filePath));
        }

        private BucketInfoDto GenerateBucketInfo(string bucketName)
        {
            return new BucketInfoDto
            {
                Name = bucketName,
                Objects = new ObjectInfoDto[0],
                Owner = null
            };
        }
        private BucketInfoDto GetBucketInfo(string bucketName)
        {

            var path = GetBucketInfoPath(bucketName);

            return !File.Exists(path) ? null : JsonConvert.DeserializeObject<BucketInfoDto>(File.ReadAllText(path, Encoding.UTF8));
        }


        private class BucketInfoDto
        {
            public string Name { get; set; }
            public string Owner { get; set; }
            public ObjectInfoDto[] Objects { get; set; }
        }

        private class ObjectInfoDto
        {
            public string Name { get; set; }
            public long Size { get; set; }
            public DateTime LastModified { get; set; }
            public string ETag { get; set; }
            public string ContentType { get; set; }
            public Dictionary<string, string> MetaData { get; set; }
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
            await EnsureBucketDoesNotExistAsync(bucketName, cancellationToken);

            Directory.CreateDirectory(Path.Combine(_baseFolder, bucketName));

        }

        public async Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {

            return await Task.Run(() =>
            {
                var folders = Directory.EnumerateDirectories(_baseFolder);

                return new ListBucketsResult
                {
                    Buckets = folders.Where(item => !item.EndsWith(InfoFolder)).Select(item => new BucketInfo
                    {
                        Name = Path.GetFileName(item),
                        CreationDate = Directory.GetCreationTime(item)
                    })
                };

            }, cancellationToken);


        }

        public async Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => BucketExists(bucket), cancellationToken);
        }

        private bool BucketExists(string bucket)
        {
            return Directory.Exists(Path.Combine(_baseFolder, bucket));
        }

        public async Task RemoveBucketAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            await EnsureBucketExistsAsync(bucketName, cancellationToken);

            var fullPath = Path.Combine(_baseFolder, bucketName);

            Directory.Delete(fullPath, true);

            var bucketPolicyPath = GetBucketPolicyPath(bucketName);

            if (File.Exists(bucketPolicyPath))
                File.Delete(bucketPolicyPath);

            var bucketInfoPath = GetBucketInfoPath(bucketName);

            if (File.Exists(bucketInfoPath))
                File.Delete(bucketInfoPath);
        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {

            EnsureBucketExists(bucketName);

            var bucketPath = GetBucketPath(bucketName);
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            return Observable.Create<ItemInfo>(obs =>
            {

                var path = prefix != null ? Path.Combine(bucketPath, prefix) : bucketPath;

                var files = Directory.EnumerateFiles(path, "*", searchOption);

                foreach (var file in files)
                {

                    var info = new FileInfo(file);

                    var obj = new ItemInfo
                    {
                        Key = Path.GetFileName(file),
                        Size = (ulong)info.Length,
                        LastModified = info.LastWriteTime,
                        IsDir = false
                    };

                    obs.OnNext(obj);
                }

                var folders = Directory.EnumerateDirectories(path, "*", searchOption);

                foreach (var folder in folders)
                {

                    var obj = new ItemInfo
                    {
                        Key = Path.GetFileName(folder),
                        Size = 0,
                        LastModified = Directory.GetLastWriteTime(folder),
                        IsDir = true
                    };

                    obs.OnNext(obj);
                }

                obs.OnCompleted();

                // Cold observable
                return () => { };

            });

        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {

            await EnsureBucketExistsAsync(bucketName, cancellationToken);

            var bucketPolicyPath = GetBucketPolicyPath(bucketName);

            if (File.Exists(bucketPolicyPath))
            {
                return await File.ReadAllTextAsync(bucketPolicyPath, Encoding.UTF8, cancellationToken);
            }

            return null;

        }


        public async Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {

            await EnsureBucketExistsAsync(bucketName, cancellationToken);

            var bucketPolicyPath = GetBucketPolicyPath(bucketName);

            try
            {
                JObject.Parse(policyJson);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid policy json", ex);
            }

            await File.WriteAllTextAsync(bucketPolicyPath, policyJson, Encoding.UTF8, cancellationToken);

        }

        #region Utils

        private void CheckPath(string path)
        {
            if (path.Contains(".."))
                throw new InvalidOperationException("Parent path separator is not supported");
        }
        private string GetBucketPolicyPath(string bucketName)
        {
            return Path.Combine(_infoFolderPath, $"{bucketName}-{PolicySuffix}.json");
        }

        private string GetBucketInfoPath(string bucketName)
        {
            return Path.Combine(_infoFolderPath, $"{bucketName}.json");
        }

        private string GetBucketPath(string bucketName)
        {
            return Path.Combine(_baseFolder, bucketName);
        }

        private async Task EnsureBucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            var bucketExists = await BucketExistsAsync(bucketName, cancellationToken);

            if (!bucketExists)
                throw new ArgumentException($"Bucket '{bucketName}' does not exist");

        }

        private void EnsurePathExists(string path)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"Object '{Path.GetFileName(path)}' does not exist");

        }

        private async Task EnsureBucketDoesNotExistAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            var bucketExists = await BucketExistsAsync(bucketName, cancellationToken);

            if (bucketExists)
                throw new ArgumentException($"Bucket '{bucketName}' already existing");

        }

        private void EnsureBucketExists(string bucketName)
        {
            var bucketExists = BucketExists(bucketName);

            if (!bucketExists)
                throw new ArgumentException($"Bucket '{bucketName}' does not exist");

        }

        private void EnsureBucketDoesNotExist(string bucketName)
        {
            var bucketExists = BucketExists(bucketName);

            if (bucketExists)
                throw new ArgumentException($"Bucket '{bucketName}' already existing");

        }

        #endregion
        /*
        public void CreateDirectory(string bucket, string path)
        {
           CheckPath(path);

           Directory.CreateDirectory(Path.Combine(_baseFolder, bucket, path));
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
        }*/
    }
}
