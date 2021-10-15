using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem.Model
{
    public class CachedS3ObjectSystem : IObjectSystem
    {
        private readonly CachedS3ObjectSystemSettings _settings;
        private readonly ILogger<CachedS3ObjectSystem> _logger;
        private readonly S3ObjectSystem _remoteStorage;

        private readonly string _globalLockFilePath;

        private const string FilesFolderName = "files";
        private const string PoliciesFolderName = "policies";
        private const string PendingPoliciesFolderName = "pending";

        private const string GlobalLockFileName = "semaphore.lock";
        
        public CachedS3ObjectSystem(CachedS3ObjectSystemSettings settings, ILogger<CachedS3ObjectSystem> logger)
        {
            _settings = settings;
            _logger = logger;
            LogInformation = s => _logger.LogInformation(s);
            LogError = (ex, s) => _logger.LogError(ex, s);

            _globalLockFilePath = Path.GetFullPath(Path.Combine(settings.CachePath, GlobalLockFileName));
            
            Directory.CreateDirectory(_settings.CachePath);

            _remoteStorage = new S3ObjectSystem(settings);
        }

        public Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task RemoveObjectsAsync(string bucketName, string[] objectsNames, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> ObjectExistsAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            CopyConditions copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task RemoveBucketAsync(string bucketName, bool force = true, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetPolicyAsync(bucketName, cancellationToken);
        }

        public Task SetPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

        public StorageInfo GetStorageInfo()
        {
            return _remoteStorage.GetStorageInfo();
        }

        public void Cleanup()
        {
            throw new NotImplementedException();
        }

        public bool IsS3Based()
        {
            return true;
        }

        public string GetInternalPath(string bucketName, string objectName)
        {
            return GetCachedFilePath(bucketName, objectName);
        }
        
        
        #region Utils 
        
        private readonly Action<string> LogInformation;
        private readonly Action<Exception, string> LogError;

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
        
        private string GetPolicyFilePath(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName, PoliciesFolderName, objectName + ".json"));
        }
        
        private string GetPendingPolicyFilePath(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName, PendingPoliciesFolderName, objectName + ".json"));
        }

        private string SmartGetPolicyFilePath(string bucketName, string objectName)
        {
            var policy = GetPolicyFilePath(bucketName, objectName);
            var pendingPolicy = GetPendingPolicyFilePath(bucketName, objectName);

            return File.Exists(policy) ? policy : File.Exists(pendingPolicy) ? pendingPolicy : null;
        }
        
        private class ObjectPolicy
        {
            public DateTime? SyncTime { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
            public Error LastError { get; set; }

            [JsonIgnore]
            public bool IsSyncronized => SyncTime != null;
            
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