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
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    /// <summary>
    /// Physical implementation of object system interface
    /// </summary>
    public class PhysicalObjectSystem : IObjectSystem
    {
        public bool UseStrictNamingConvention { get; }
        private readonly string _baseFolder;

        private const string ContentTypeKey = "Content-Type";
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

        public void SyncBucket(string bucketName)
        {
            EnsureBucketExists(bucketName);

            var bucketPath = GetBucketPath(bucketName); 

            foreach (var file in Directory.EnumerateFiles(bucketPath, "*.*", SearchOption.AllDirectories))
            {

                var objectName = file.Replace(bucketPath, string.Empty);
                if (objectName.StartsWith('/') || objectName.StartsWith('\\')) objectName = objectName.Substring(1);

                UpdateObjectInfo(bucketName, objectName);
            }
        }

        #region Objects

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);
            var objectPath = EnsureObjectExists(bucketName, objectName);

            await using var stream = File.OpenRead(objectPath);

            callback(stream);

        }

        public async Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> callback,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);
            var objectPath = EnsureObjectExists(bucketName, objectName);

            await using var stream = File.OpenRead(objectPath);

            var buffer = new byte[length];

            await stream.ReadAsync(buffer, (int)offset, (int)length, cancellationToken);

            await using var memory = new MemoryStream(buffer);

            callback(memory);
        }


        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);
            var objectPath = EnsureObjectExists(bucketName, objectName);

            await Task.Run(() =>
            {
                File.Delete(objectPath);

                // Remove from bucket json
                RemoveObjectInfoInternal(bucketName, objectName);
            }, cancellationToken);

        }


        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);
            EnsureObjectExists(bucketName, objectName);

            return await Task.Run(() =>
            {
                var bucketInfo = GetOrGenerateBucketInfo(bucketName);

                var objectInfo = bucketInfo.Objects.FirstOrDefault(item => item.Name == objectName) ??
                                 UpdateObjectInfo(bucketName, objectName);

                return new ObjectInfo(
                    objectInfo.Name,
                    objectInfo.Size,
                    objectInfo.LastModified,
                    objectInfo.ETag,
                    objectInfo.ContentType,
                    objectInfo.MetaData);

            }, cancellationToken);

        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);

            // We never have incomplete uploads :)
            return new ObjectUpload[0].ToObservable();
        }

#pragma warning disable 1998
        public async Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
#pragma warning restore 1998
        {
            EnsureBucketExists(bucketName);
            EnsureObjectExists(bucketName, objectName);

            throw new NotSupportedException("This adapter does not support incomplete uploads");
        }

        public async Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            IReadOnlyDictionary<string, string> copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);
            var objectPath = EnsureObjectExists(bucketName, objectName);

            EnsureBucketExists(destBucketName);

            // TODO: Implement copy condition
            if (copyConditions != null)
                throw new NotImplementedException("Copy conditions are not supported");

            destObjectName ??= objectName;

            var destPath = GetObjectPath(destBucketName, destObjectName);

            var destFolder = Path.GetDirectoryName(destPath);

            if (!Directory.Exists(destFolder))
                Directory.CreateDirectory(destFolder);

            File.Copy(objectPath, destPath, true);

            var info = await GetObjectInfoAsync(bucketName, objectName, sseSrc, cancellationToken);

            var newInfo = new ObjectInfoDto
            {
                Name = info.ObjectName,
                Size = info.Size,
                LastModified = info.LastModified,
                ETag = info.ETag,
                ContentType = info.ContentType,
                MetaData = info.MetaData ?? metadata ?? new Dictionary<string, string>()
            };

            AddOrReplaceObjectInfoInternal(destBucketName, newInfo);

        }
        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);

            var objectPath = GetObjectPath(bucketName, objectName);
            var fileDirectory = Path.GetDirectoryName(objectPath);

            if (!Directory.Exists(fileDirectory))
                Directory.CreateDirectory(fileDirectory);

            await using var writer = File.OpenWrite(objectPath);

            data.Reset();
            await data.CopyToAsync(writer, cancellationToken);

            writer.Close();

            if (contentType != null || metaData != null)
            {

                var objectInfo = await GetObjectInfoAsync(bucketName, objectName, cancellationToken: cancellationToken);

                var newInfo = new ObjectInfoDto
                {
                    ContentType = contentType ?? objectInfo.ContentType,
                    ETag = objectInfo.ETag,
                    LastModified = objectInfo.LastModified,
                    MetaData = metaData ?? objectInfo.MetaData ?? new Dictionary<string, string>(),
                    Name = objectName,
                    Size = objectInfo.Size
                };

                AddOrReplaceObjectInfoInternal(bucketName, newInfo);
            }
            else
            {
                UpdateObjectInfo(bucketName, objectName);
            }


        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            EnsureFileExists(filePath);

            await using var reader = File.OpenRead(filePath);

            await PutObjectAsync(bucketName, objectName, reader, new FileInfo(filePath).Length, contentType, metaData,
                sse, cancellationToken);

        }

        public async Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);
            var objectPath = EnsureObjectExists(bucketName, objectName);

            await Task.Run(() =>
            {
                File.Copy(objectPath, filePath, true);
            }, cancellationToken);

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
                var fullPath = Path.GetFullPath(path);

                foreach (var file in files)
                {

                    var info = new FileInfo(file);

                    var key = info.FullName.Replace(fullPath, string.Empty);

                    if (key.StartsWith("/") || key.StartsWith("\\"))
                        key = key.Substring(1, key.Length - 1);

                    var obj = new ItemInfo
                    {
                        Key = key,
                        Size = (ulong)info.Length,
                        LastModified = info.LastWriteTime,
                        IsDir = false
                    };

                    obs.OnNext(obj);
                }

                var folders = Directory.EnumerateDirectories(path, "*", searchOption);

                foreach (var folder in folders)
                {

                    var key = Path.GetFullPath(folder).Replace(fullPath, string.Empty);

                    if (key.StartsWith("/") || key.StartsWith("\\"))
                        key = key.Substring(1, key.Length - 1);

                    var obj = new ItemInfo
                    {
                        Key = key,
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

        #endregion

        #region Buckets

        public async Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default)
        {
            EnsureBucketDoesNotExist(bucketName);

            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.Combine(_baseFolder, bucketName));

                UpdateBucketInfo(bucketName);

            }, cancellationToken);

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

        public Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => BucketExists(bucketName), cancellationToken);
        }

        private bool BucketExists(string bucket)
        {
            return Directory.Exists(Path.Combine(_baseFolder, bucket));
        }

        public async Task RemoveBucketAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            EnsureBucketExists(bucketName);

            await Task.Run(() =>
            {
                var fullPath = GetBucketPath(bucketName);

                Directory.Delete(fullPath, true);

                var bucketPolicyPath = GetBucketPolicyPath(bucketName);

                if (File.Exists(bucketPolicyPath))
                    File.Delete(bucketPolicyPath);

                var bucketInfoPath = GetBucketInfoPath(bucketName);

                if (File.Exists(bucketInfoPath))
                    File.Delete(bucketInfoPath);

            }, cancellationToken);
        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {

            EnsureBucketExists(bucketName);

            var bucketPolicyPath = GetBucketPolicyPath(bucketName);

            if (File.Exists(bucketPolicyPath))
            {
                return await File.ReadAllTextAsync(bucketPolicyPath, Encoding.UTF8, cancellationToken);
            }

            return null;

        }
        
        public async Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {

            EnsureBucketExists(bucketName);

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

        public StorageInfo GetStorageInfo()
        {
            var f = new FileInfo(_baseFolder);
            var drive = Path.GetPathRoot(f.FullName);

            // This could be improved but It's enough for now
            var info = DriveInfo.GetDrives()
                .FirstOrDefault(drv => 
                    string.Equals(drv.Name, drive, StringComparison.OrdinalIgnoreCase));

            return info == null ? null : new StorageInfo(info.TotalSize, info.AvailableFreeSpace);
        }

        #endregion

        #region Utils


        private string EnsureObjectExists(string bucketName, string objectName)
        {
            CheckPath(objectName);
            var objectPath = GetObjectPath(bucketName, objectName);

            EnsureFileExists(objectPath);
            return objectPath;
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

        private BucketInfoDto GetOrGenerateBucketInfo(string bucketName)
        {
            return GetBucketInfo(bucketName) ?? GenerateBucketInfo(bucketName);
        }

        /// <summary>
        /// Updates bucket info cache
        /// </summary>
        /// <param name="bucketName"></param>
        /// <returns></returns>
        private BucketInfoDto UpdateBucketInfo(string bucketName)
        {
            var bucketInfo = GenerateBucketInfo(bucketName);
            
            SaveBucketInfo(bucketInfo);

            return bucketInfo;
        }

        private void SaveBucketInfo(BucketInfoDto bucketInfo)
        {
            var bucketInfoPath = GetBucketInfoPath(bucketInfo.Name);
            File.WriteAllText(bucketInfoPath, JsonConvert.SerializeObject(bucketInfo, Formatting.Indented));
        }

        private void AddOrReplaceObjectInfoInternal(string bucketName, ObjectInfoDto objectInfo)
        {
            var bucketInfo = GetOrGenerateBucketInfo(bucketName);

            // Content type is a system-defined object metadata
            if (objectInfo.MetaData.ContainsKey(ContentTypeKey))
            {
                objectInfo.ContentType = objectInfo.MetaData[ContentTypeKey];
                var temp = new Dictionary<string, string>(objectInfo.MetaData);
                temp.Remove(ContentTypeKey);
                objectInfo.MetaData = temp;
            }

            // Replace object
            bucketInfo.Objects = bucketInfo.Objects.Where(item => item.Name != objectInfo.Name).Concat(new[] { objectInfo }).ToArray();

            SaveBucketInfo(bucketInfo);
        }

        private void RemoveObjectInfoInternal(string bucketName, string objectName)
        {
            var bucketInfo = GetBucketInfo(bucketName);
            var bucketInfoPath = GetBucketInfoPath(bucketName);

            // Remove object
            bucketInfo.Objects = bucketInfo.Objects.Where(item => item.Name != objectName).ToArray();

            File.WriteAllText(bucketInfoPath, JsonConvert.SerializeObject(bucketInfo, Formatting.Indented));

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

            AddOrReplaceObjectInfoInternal(bucketName, objectInfo);

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

        public static string CalculateETag(string filePath, FileInfo info)
        {
            // 5GB
            var chunkSize = 5L * 1024 * 1024 * 1024;

            var parts = info.Length == 0 ? 1 : (int)Math.Ceiling((double)info.Length / chunkSize);

            return AdaptersUtils.CalculateMultipartEtag(File.ReadAllBytes(filePath), parts);

        }

        private string CalculateETag(string filePath)
        {
            return CalculateETag(filePath, new FileInfo(filePath));
        }

        private void CheckPath(string path)
        {
            if (path.Contains(".."))
                throw new ArgumentException("Parent path separator is not supported");
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

        private void EnsureFileExists(string path)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"File '{Path.GetFileName(path)}' does not exist");
        }

        private void EnsureFolderExists(string path)
        {
            if (!Directory.Exists(path))
                throw new ArgumentException($"Folder '{Path.GetFileName(path)}' does not exist");
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


        public class BucketInfoDto
        {
            public string Name { get; set; }
            public string Owner { get; set; }
            public ObjectInfoDto[] Objects { get; set; }
        }

        public class ObjectInfoDto
        {
            public string Name { get; set; }
            public long Size { get; set; }
            public DateTime LastModified { get; set; }
            public string ETag { get; set; }
            public string ContentType { get; set; }
            public IReadOnlyDictionary<string, string> MetaData { get; set; }
        }

        #endregion

    }
}
