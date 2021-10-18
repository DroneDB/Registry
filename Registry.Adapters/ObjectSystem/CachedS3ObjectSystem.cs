using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
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

        private const int FileRetries = 1200;
        private const int FileRetriesDelay = 50;

        private const int RemoteCallRetries = 3;

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
            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);

            LogInformation(
                $"In GetObjectAsync('{bucketName}', '{objectName}')");
            LogInformation($"Descriptor = '{descriptorFilePath}'");
            LogInformation($"CachedFile = '{cachedFilePath}'");

            try
            {
                await using var descriptorStream = File.OpenRead(descriptorFilePath);
                await using var stream = File.OpenRead(cachedFilePath);

                LogInformation("Cache HIT: returning cached file");
                callback(stream);
            }
            catch (IOException ex)
            {
                if (ex is DirectoryNotFoundException or FileNotFoundException)
                {
                    LogInformation("Cache MISS: trying to retrieve file");

                    await CacheFileAndReturn(bucketName, objectName, callback, sse, cancellationToken);
                }
                else throw;
            }
        }

        private async Task CacheFileAndReturn(string bucketName, string objectName, Action<Stream> callback,
            IServerEncryption sse,
            CancellationToken cancellationToken)
        {
            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            try
            {
                EnsurePathExists(cachedFilePath);
                EnsurePathExists(descriptorFilePath);

                await using var descriptorStream =
                    new FileStream(descriptorFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var descriptorWriter = new StreamWriter(descriptorStream);
                await using var fileStream = new FileStream(cachedFilePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None);

                LogInformation("Getting file from remote");

                await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                    (exception, i) => LogInformation($"Retrying S3 download ({i}): {exception.Message}")
                ).ExecuteAsync(async () =>
                    await _remoteStorage.GetObjectAsync(bucketName, objectName, s =>
                    {
                        LogInformation("Running callback");

                        callback(s);
                        s.Reset();
                        LogInformation("Copying to cache file");

                        s.CopyTo(fileStream);

                        var obj = new ObjectDescriptor
                        {
                            LastError = null,
                            SyncTime = DateTime.Now
                        };

                        LogInformation("Writing descriptor");

                        descriptorWriter.Write(JsonConvert.SerializeObject(obj));

                        s.Close();
                        LogInformation("Callback OK");
                    }, sse, cancellationToken));
            }
            catch (IOException ex)
            {
                LogInformation($"Suppressed IOException: '{ex.Message}'");
                LogInformation("File already existing, waiting for it to become available");

                await using var stream =
                    await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        FileRetriesDelay, FileRetries);

                if (stream == null)
                    throw new InvalidOperationException("Cannot get file from S3, timeout reached");

                LogInformation("File is now available, running callback");
                callback(stream);
            }
        }

        private async Task CacheFileAndReturn(string bucketName, string objectName, string filePath,
            IServerEncryption sse,
            CancellationToken cancellationToken)
        {
            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            try
            {
                EnsurePathExists(cachedFilePath);
                EnsurePathExists(descriptorFilePath);

                await using var descriptorStream =
                    new FileStream(descriptorFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var descriptorWriter = new StreamWriter(descriptorStream);

                LogInformation("Getting file from remote");

                await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                    (exception, i) => LogInformation($"Retrying S3 download ({i}): {exception.Message}")
                ).ExecuteAsync(async () =>
                    await _remoteStorage.GetObjectAsync(bucketName, objectName, filePath, sse, cancellationToken));

                File.Copy(filePath, cachedFilePath);

                var obj = new ObjectDescriptor
                {
                    LastError = null,
                    SyncTime = DateTime.Now
                };

                LogInformation("Writing descriptor");

                await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

                LogInformation("Descriptor written OK");
            }
            catch (IOException ex)
            {
                LogInformation($"Suppressed IOException: '{ex.Message}'");
                LogInformation("File already existing, waiting for it to become available");

                await using var stream =
                    await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        FileRetriesDelay, FileRetries);

                if (stream == null)
                    throw new InvalidOperationException("Cannot get file from S3, timeout reached");

                LogInformation("File is now available, copy from cache");

                File.Copy(cachedFilePath, filePath, true);
            }
        }

        public async Task GetObjectAsync(string bucketName, string objectName, string filePath,
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);

            LogInformation(
                $"In GetObjectAsync('{bucketName}', '{objectName}', '{filePath}')");
            LogInformation($"Descriptor = '{descriptorFilePath}'");
            LogInformation($"CachedFile = '{cachedFilePath}'");

            try
            {
                await using var descriptorStream = File.OpenRead(descriptorFilePath);
                await using var stream = File.OpenRead(cachedFilePath);

                LogInformation("Cache HIT: returning cached file");

                await using var output = File.OpenWrite(filePath);
                await stream.CopyToAsync(output, cancellationToken);
            }
            catch (IOException ex)
            {
                if (ex is DirectoryNotFoundException or FileNotFoundException)
                {
                    LogInformation("Cache MISS: trying to retrieve file");

                    await CacheFileAndReturn(bucketName, objectName, filePath, sse, cancellationToken);
                }
                else throw;
            }
        }

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size,
            string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);

            EnsurePathExists(descriptorFilePath);
            EnsurePathExists(cachedFilePath);

            await using var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);

            if (descriptorStream == null)
                throw new InvalidOperationException("Cannot lock local file, timeout reached");

            await using var fileStream =
                await CommonUtils.WaitForFile(cachedFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);

            if (fileStream == null)
                throw new InvalidOperationException("Cannot lock local descriptor file, timeout reached");

 // Empty descriptor
            descriptorStream.SetLength(0);

            // Empty file
            fileStream.SetLength(0);

            await using var descriptorWriter = new StreamWriter(descriptorStream);

            // This file is not synced yet, we take note about it
            var obj = new ObjectDescriptor
            {
                LastError = null,
                SyncTime = null,
                Info = new UploadInfo
                {
                    ContentType = contentType,
                    MetaData = metaData,
                    SSE = sse
                }
            };

            await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

            // TODO: We are ignoring the lenght parameter, this could lead to problems if length != stream.Length
            await data.CopyToAsync(fileStream, cancellationToken);
        }
        
        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            
            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);

            EnsurePathExists(descriptorFilePath);
            EnsurePathExists(cachedFilePath);

            await using var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);

            if (descriptorStream == null)
                throw new InvalidOperationException("Cannot lock local file, timeout reached");

            // Empty descriptor
            descriptorStream.SetLength(0);

            await using var descriptorWriter = new StreamWriter(descriptorStream);

            // This file is not synced yet, we take note about it
            var obj = new ObjectDescriptor
            {
                LastError = null,
                SyncTime = null,
                Info = new UploadInfo
                {
                    ContentType = contentType,
                    MetaData = metaData,
                    SSE = sse
                }
            };

            await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

            File.Copy(filePath, cachedFilePath, true);
           

        }

        public async Task Sync()
        {
            
            // Go throught all the files and sync / remove
            throw new NotImplementedException();
            // We try to open or create the file descriptor with exclusive access
            /*await using var descriptorStream = new FileStream(descriptorFilePath, FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None);
            await using var fileStream = new FileStream(cachedFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.None);*/

            //   return _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse,
            //       cancellationToken);

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

#pragma warning disable 1998
        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName,
#pragma warning restore 1998
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            var key = GetMemoryKey(bucketName, objectName);

            return _objectInfos.GetOrAdd(key, static (s, info) =>
                    info.Storage.GetObjectInfoAsync(info.bucketName, info.objectName, info.sse, info.cancellationToken)
                        .Result,
                new { Storage = _remoteStorage, bucketName, objectName, sse, cancellationToken });
        }




        public Task RemoveBucketAsync(string bucketName, bool force = true,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.RemoveBucketAsync(bucketName, force, cancellationToken);
        }


        #region Proxied

        public Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetObjectAsync(bucketName, objectName, offset, length, cb, sse, cancellationToken);
        }

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
            
            // Enforce cache limits
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

            public UploadInfo Info { get; set; }

            [JsonIgnore] public bool IsSyncronized => SyncTime != null;
            [JsonIgnore] public bool IsError => LastError != null;
        }

        private class UploadInfo
        {
            public string ContentType { get; set; }
            public Dictionary<string, string> MetaData { get; set; }
            public IServerEncryption SSE { get; set; }
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