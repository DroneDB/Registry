using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem
{
    public class CachedS3ObjectSystem : IObjectSystem
    {
        private readonly CachedS3ObjectSystemSettings _settings;
        private readonly ILogger<CachedS3ObjectSystem> _logger;
        private readonly IObjectSystem _remoteStorage;

        private readonly string _globalLockFilePath;

        private const string FilesFolderName = "files";
        private const string DescriptorsFolderName = "descriptors";

        private const string GlobalLockFileName = "semaphore.lock";

        private readonly ConcurrentDictionary<string, ObjectInfo> _objectInfos;

        public CachedS3ObjectSystem(CachedS3ObjectSystemSettings settings, Func<IObjectSystem> objectSystemFactory,
            ILogger<CachedS3ObjectSystem> logger)
        {
            _settings = settings;
            _logger = logger;
            LogInformation = s => _logger.LogInformation(s);
            LogError = (ex, s) => _logger.LogError(ex, s);

            _globalLockFilePath = Path.GetFullPath(Path.Combine(settings.CachePath, GlobalLockFileName));

            Directory.CreateDirectory(_settings.CachePath);

            _remoteStorage = objectSystemFactory();
            _objectInfos = new ConcurrentDictionary<string, ObjectInfo>();
        }

        public CachedS3ObjectSystem(CachedS3ObjectSystemSettings settings, ILogger<CachedS3ObjectSystem> logger) :
            this(settings, () => new S3ObjectSystem(settings), logger)
        {
        }

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback,
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var descriptor = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);

            try
            {
                var stream = File.OpenRead(cachedFilePath);
                callback(stream);
            }
            catch (IOException ex)
            {
                if (ex is DirectoryNotFoundException or FileNotFoundException)
                {
                    EnsurePathExists(cachedFilePath);
                    EnsurePathExists(descriptor);

                    await using var descriptorStream =
                        new FileStream(descriptor, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var descriptorWriter = new StreamWriter(descriptorStream);
                    await using var fileStream = new FileStream(cachedFilePath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None);

                    await _remoteStorage.GetObjectAsync(bucketName, objectName, (s) =>
                    {
                        callback(s);
                        s.Reset();
                        s.CopyTo(fileStream);

                        var obj = new ObjectDescriptor
                        {
                            LastError = null,
                            SyncTime = DateTime.Now
                        };

                        descriptorWriter.Write(JsonConvert.SerializeObject(obj));

                    }, sse, cancellationToken);
                }
            }

            /*using (var stream = CommonUtils.WaitForFile(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
            }*/
        }

        public Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetObjectAsync(bucketName, objectName, offset, length, cb, sse, cancellationToken);
        }

        public Task PutObjectAsync(string bucketName, string objectName, Stream data, long size,
            string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse,
                cancellationToken);
        }

        public Task RemoveObjectAsync(string bucketName, string objectName,
            CancellationToken cancellationToken = default)
        {
            var key = GetMemoryKey(bucketName, objectName);

            _objectInfos.TryRemove(key, out _);

            return _remoteStorage.RemoveObjectAsync(bucketName, objectName, cancellationToken);
        }

        public Task RemoveObjectsAsync(string bucketName, string[] objectsNames,
            CancellationToken cancellationToken = default)
        {
            foreach (var name in objectsNames)
            {
                var key = GetMemoryKey(bucketName, name);
                _objectInfos.TryRemove(key, out _);
            }

            return _remoteStorage.RemoveObjectsAsync(bucketName, objectsNames, cancellationToken);
        }

        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName,
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var key = GetMemoryKey(bucketName, objectName);

            return _objectInfos.GetOrAdd(key,
                s => _remoteStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken).Result);
        }

        public Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse,
                cancellationToken);
        }

        public Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetObjectAsync(bucketName, objectName, filePath, sse, cancellationToken);
        }

        public Task RemoveBucketAsync(string bucketName, bool force = true,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.RemoveBucketAsync(bucketName, force, cancellationToken);
        }


        #region Proxied

        public Task<bool> ObjectExistsAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ObjectExistsAsync(bucketName, objectName, sse, cancellationToken);
        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "",
            bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListIncompleteUploads(bucketName, prefix, recursive, cancellationToken);
        }

        public Task RemoveIncompleteUploadAsync(string bucketName, string objectName,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.RemoveIncompleteUploadAsync(bucketName, objectName, cancellationToken);
        }

        public Task CopyObjectAsync(string bucketName, string objectName, string destBucketName,
            string destObjectName = null,
            CopyConditions copyConditions = null, Dictionary<string, string> metadata = null,
            IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.CopyObjectAsync(bucketName, objectName, destBucketName, destObjectName,
                copyConditions, metadata,
                sseSrc, sseDest, cancellationToken);
        }

        public Task MakeBucketAsync(string bucketName, string location = null,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.MakeBucketAsync(bucketName, location, cancellationToken);
        }

        public Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListBucketsAsync(cancellationToken);
        }

        public Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.BucketExistsAsync(bucketName, cancellationToken);
        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListObjectsAsync(bucketName, prefix, recursive, cancellationToken);
        }

        public Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetPolicyAsync(bucketName, cancellationToken);
        }

        public Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

        #endregion

        public StorageInfo GetStorageInfo()
        {
            return _remoteStorage.GetStorageInfo();
        }

        public void Cleanup()
        {
            _remoteStorage.Cleanup();
        }

        public bool IsS3Based()
        {
            return true;
        }

        public string GetInternalPath(string bucketName, string objectName)
        {
            return _settings.BridgeUrl + "/" + bucketName + "/" + Uri.EscapeUriString(objectName);
        }


        #region Utils

        private readonly Action<string> LogInformation;
        private readonly Action<Exception, string> LogError;

        public string GetMemoryKey(string bucketName, string objectName)
        {
            return bucketName + "-" + objectName;
        }

        private void EnsurePathExists(string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (folder != null)
                Directory.CreateDirectory(folder);
        }

        private void EnsureBucketPathExists(string bucketName)
        {
            var bucketPath = GetBucketFolder(bucketName);
            Directory.CreateDirectory(bucketPath);
        }

        private string GetBucketFolder(string bucketName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName));
        }

        private string GetCachedFilePath(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName, FilesFolderName, objectName));
        }

        private string GetDescriptorFilePath(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName, DescriptorsFolderName,
                objectName + ".json"));
        }

        private class ObjectDescriptor
        {
            public DateTime? SyncTime { get; set; }
            public Error LastError { get; set; }

            [JsonIgnore] public bool IsSyncronized => SyncTime != null;
        }

        private class Error
        {
            public string Message { get; set; }
            public DateTime When { get; set; }

            public override string ToString()
            {
                return $"[{When:yyyy-MM-dd-HH-mm-ss}] {Message}";
            }
        }

        #endregion
    }
}