using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Minio.Exceptions;
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
        private const string TbdFolderName = "tbd";
        private const string PendingFolderName = "pending";

        private const string LockFileName = "sync.lock";
        private const string BucketDeleteFlagFileName = "tbd.lock";

        private const int FileRetries = 1200;
        private const int FileRetriesDelay = 50;

        private const int RemoteCallRetries = 3;

        private readonly ConcurrentDictionary<string, ObjectInfo> _objectInfos;

        #region Ctor

        public CachedS3ObjectSystem(CachedS3ObjectSystemSettings settings, Func<IObjectSystem> objectSystemFactory,
            ILogger<CachedS3ObjectSystem> logger)
        {
            _settings = settings;
            _logger = logger;
            LogInformation = s => _logger.LogInformation(s);
            LogError = (ex, s) => _logger.LogError(ex, s);

            _globalLockFilePath = Path.GetFullPath(Path.Combine(settings.CachePath, LockFileName));

            Directory.CreateDirectory(_settings.CachePath);

            _remoteStorage = objectSystemFactory();
            _objectInfos = new ConcurrentDictionary<string, ObjectInfo>();
        }

        public CachedS3ObjectSystem(CachedS3ObjectSystemSettings settings, ILogger<CachedS3ObjectSystem> logger) :
            this(settings, () => new S3ObjectSystem(settings), logger)
        {
        }

        #endregion

        #region GetObject

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback,
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);
            HandleFileToBeDeleted(bucketName, objectName);

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

        public async Task GetObjectAsync(string bucketName, string objectName, string filePath,
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);
            HandleFileToBeDeleted(bucketName, objectName);

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

        #endregion

        #region PutObject

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size,
            string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            var pendingFilePath = GetPendingFilePath(bucketName, objectName);

            EnsurePathExists(descriptorFilePath);
            EnsurePathExists(cachedFilePath);
            EnsurePathExists(pendingFilePath);

            await using (var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries))
            {
                if (descriptorStream == null)
                    throw new InvalidOperationException("Cannot lock local descriptor file, timeout reached");

                await using var fileStream =
                    await CommonUtils.WaitForFile(cachedFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                        FileShare.None, FileRetriesDelay, FileRetries);

                if (fileStream == null)
                    throw new InvalidOperationException("Cannot lock local file, timeout reached");

                // Delete any tbd file
                ClearFileTbdFlag(bucketName, objectName);

                // Empty descriptor
                descriptorStream.SetLength(0);

                // Empty file
                fileStream.SetLength(0);

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

                await using var descriptorWriter = new StreamWriter(descriptorStream);
                await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

                // TODO: We are ignoring the lenght parameter, this could lead to problems if length != stream.Length
                await data.CopyToAsync(fileStream, cancellationToken);
                
                await File.WriteAllTextAsync(pendingFilePath, DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"), cancellationToken);
                
            }

            CommonUtils.SafeDelete(cachedFilePath);
        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath,
            string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            var pendingFilePath = GetPendingFilePath(bucketName, objectName);

            EnsurePathExists(descriptorFilePath);
            EnsurePathExists(cachedFilePath);
            EnsurePathExists(pendingFilePath);

            await using var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);

            if (descriptorStream == null)
                throw new InvalidOperationException("Cannot lock local file, timeout reached");

            ClearFileTbdFlag(bucketName, objectName);

            // Empty descriptor
            descriptorStream.SetLength(0);

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

            await using var descriptorWriter = new StreamWriter(descriptorStream);
            await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

            File.Copy(filePath, cachedFilePath, true);
            await File.WriteAllTextAsync(pendingFilePath, DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"), cancellationToken);

        }

        #endregion

        #region RemoveObject

        public async Task RemoveObjectAsync(string bucketName, string objectName,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            var tbdFilePath = GetTbdFilePath(bucketName, objectName);

            EnsurePathExists(descriptorFilePath);
            EnsurePathExists(cachedFilePath);
            EnsurePathExists(tbdFilePath);

            if (File.Exists(tbdFilePath) || !File.Exists(descriptorFilePath))
                return;

            await using var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);

            descriptorStream.SetLength(0);

            if (descriptorStream == null)
                throw new InvalidOperationException("Cannot lock local file, timeout reached");

            // Delete cached file
            CommonUtils.SafeDelete(cachedFilePath);

            // Mark it for deletion
            await File.WriteAllTextAsync(tbdFilePath, $"{bucketName}/${objectName}", cancellationToken);

            // Remove object info (if exists)
            var key = GetMemoryKey(bucketName, objectName);
            _objectInfos.TryRemove(key, out _);
        }

        public Task RemoveObjectsAsync(string bucketName, string[] objectsNames,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            return Task.WhenAll(
                objectsNames.Distinct().Select(objectName =>
                    RemoveObjectAsync(bucketName, objectName, cancellationToken)));
        }

#pragma warning disable 1998
        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName,
#pragma warning restore 1998
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            var key = GetMemoryKey(bucketName, objectName);

            return _objectInfos.GetOrAdd(key, static (s, info) =>
                    info.Storage.GetObjectInfoAsync(info.bucketName, info.objectName, info.sse, info.cancellationToken)
                        .Result,
                new { Storage = _remoteStorage, bucketName, objectName, sse, cancellationToken });
        }

        #endregion

        public async Task RemoveBucketAsync(string bucketName, bool force = true,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);
            var tbdFilePath = GetBucketTbdFilePath(bucketName);

            await using var stream =
                await CommonUtils.WaitForFile(tbdFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
                    FileRetriesDelay, FileRetries);

            if (stream == null)
                throw new InvalidOperationException($"Cannot lock bucket tbd file '{tbdFilePath}'");

            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(bucketName);
        }

        #region Sync

        public async Task SyncBucket(string bucketName)
        {
            var bucketFolderPath = GetBucketFolder(bucketName);
            var bucketLockFilePath = Path.Combine(bucketFolderPath, LockFileName);
            var tbdLockFilePath = GetBucketTbdFilePath(bucketName);

            try
            {
                await using (var bucketLockFileStream = new FileStream(bucketLockFilePath, FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await using var writer = new StreamWriter(bucketLockFileStream);
                    await writer.WriteAsync(DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));

                    LogInformation($"Acquired bucket '{bucketName}' lock");

                    if (File.Exists(tbdLockFilePath))
                    {
                        LogInformation($"Deleting bucket folder tree '{bucketFolderPath}'");
                        var pendingFiles = CommonUtils.SafeTreeDelete(bucketFolderPath);
                        LogInformation($"Files not deleted: '{string.Join(", ", pendingFiles)}'");

                        LogInformation($"Calling RemoveBucketAsync('{bucketName}')");

                        await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                            (exception, i) => LogInformation($"Retrying S3 bucket delete ({i}): {exception.Message}")
                        ).ExecuteAsync(async () =>
                            await _remoteStorage.RemoveBucketAsync(bucketName, true, default));

                        LogInformation($"Done remote remove");

                        return;
                    }

                    // Delete all the pending files with only one remote call
                    await DeleteAllTbdFiles(bucketName);

                    // Upload all the pending files
                    await UploadAllPendingFiles(bucketName);
                }

                if (!CommonUtils.SafeDelete(bucketLockFilePath))
                    throw new InvalidOperationException($"Cannot delete bucket '{bucketName}' lock, this is bad");

                LogInformation($"Released bucket '{bucketName}' lock");
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Sync for bucket '{bucketName}' already running", ex);
            }
            finally
            {
                CommonUtils.SafeDelete(bucketLockFilePath);
            }
        }

        private async Task UploadAllPendingFiles(string bucketName)
        {
            var bucketFolderPath = GetBucketFolder(bucketName);
            var pendingFolderPath = Path.Combine(bucketFolderPath, PendingFolderName);

            var pendingFiles = Directory.EnumerateFiles(pendingFolderPath, "*", SearchOption.AllDirectories);

            foreach (var pendingFilePath in pendingFiles)
            {
                var objectName = Path.GetRelativePath(pendingFolderPath, pendingFilePath);
                var objectFilePath = GetCachedFilePath(bucketName, objectName);
                var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);

                LogInformation($"Synchronizing pending object '{objectName}'");

                await using var descriptorStream =
                    await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None, FileRetriesDelay, FileRetries);

                if (descriptorStream == null)
                    throw new InvalidOperationException("Cannot lock local file, timeout reached");

                try
                {
                    ObjectDescriptor descriptor;

                    // Deserialize descriptor object from stream
                    using (var reader = new StreamReader(descriptorStream, leaveOpen: true))
                        descriptor = JsonConvert.DeserializeObject<ObjectDescriptor>(await reader.ReadToEndAsync());

                    if (descriptor?.Info == null)
                        throw new InvalidOperationException(
                            $"No upload info, invalid descriptor: '{descriptorFilePath}'");

                    if (descriptor.IsSyncronized)
                    {
                        LogInformation($"File '{pendingFilePath}' is already synchronized");
                        continue;
                    }

                    LogInformation($"'{descriptorFilePath}' needs to be syncronized");

                    try
                    {
                        // Attempt PutObject call
                        await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                            (exception, i) => LogInformation($"Retrying S3 object upload ({i}): {exception.Message}")
                        ).ExecuteAsync(async () =>
                            await _remoteStorage.PutObjectAsync(bucketName, objectName, objectFilePath,
                                descriptor.Info.ContentType, descriptor.Info.MetaData, descriptor.Info.SSE, default));

                        // Clear descriptor file
                        descriptorStream.SetLength(0);

                        // Update sync time and clear last error
                        descriptor.SyncTime = DateTime.Now;
                        descriptor.LastError = null;

                        // Write down descriptor file
                        await using var writer = new StreamWriter(descriptorStream);
                        await writer.WriteAsync(JsonConvert.SerializeObject(descriptor));

                        CommonUtils.SafeDelete(pendingFilePath);
                    }
                    catch (MinioException ex)
                    {
                        LogError(ex, "Cannot upload: minio error");

                        // Clear descriptor file
                        descriptorStream.SetLength(0);

                        // Save the error
                        descriptor.LastError = new Error { Message = ex.Message, When = DateTime.Now };
                        descriptor.SyncTime = null;

                        // Write down descriptor file
                        await using var writer = new StreamWriter(descriptorStream);
                        await writer.WriteAsync(JsonConvert.SerializeObject(descriptor));
                    }
                }
                catch (JsonException ex)
                {
                    LogError(ex, "Cannot deserialize file descriptor");
                }
                catch (Exception ex)
                {
                    LogError(ex, $"An error occurred during sync '{descriptorFilePath}'");
                }
            }
        }

        private async Task DeleteAllTbdFiles(string bucketName)
        {
            throw new NotImplementedException();
        }

        public async Task Sync()
        {
            try
            {
                await using (var globalLockFileStream = new FileStream(_globalLockFilePath, FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await using var writer = new StreamWriter(globalLockFileStream);
                    await writer.WriteAsync(DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));

                    LogInformation("Acquired global lock");

                    var buckets = Directory.EnumerateDirectories(_settings.CachePath);

                    // We could parallelize this
                    foreach (var bucket in buckets)
                    {
                        LogInformation($"Synchronizing bucket '{bucket}'");
                        await SyncBucket(bucket);
                    }
                }

                if (!CommonUtils.SafeDelete(_globalLockFilePath))
                    throw new InvalidOperationException("Cannot delete global lock, this is bad");

                LogInformation("Released global lock");
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Cannot run multiple synchronizations at once", ex);
            }


            // Go throught all the files and sync / remove
            throw new NotImplementedException();
            // We try to open or create the file descriptor with exclusive access
            /*await using var descriptorStream = new FileStream(descriptorFilePath, FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None);
            await using var fileStream = new FileStream(cachedFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.None);*/

            // await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
            //     (exception, i) => LogInformation($"Retrying S3 delete ({i}): {exception.Message}")
            // ).ExecuteAsync(async () =>
            //     await _remoteStorage.RemoveObjectAsync(bucketName, objectName, cancellationToken));


            //   return _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse,
            //       cancellationToken);
        }

        #endregion

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

        #region Misc

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

        #endregion

        #region Utils

        private readonly Action<string> LogInformation;
        private readonly Action<Exception, string> LogError;

        public string GetMemoryKey(string bucketName, string objectName)
        {
            return bucketName + "-" + objectName;
        }

        private string GetBucketTbdFilePath(string bucketName)
        {
            return Path.Combine(GetBucketFolder(bucketName), BucketDeleteFlagFileName);
        }

        private void HandleBucketToBeDeleted(string bucketName)
        {
            var tbdFilePath = GetBucketTbdFilePath(bucketName);

            if (File.Exists(tbdFilePath))
                throw new InvalidOperationException(
                    "Cannot perform this operation on this bucket because it is being deleted");
        }

        private void HandleFileToBeDeleted(string bucketName, string objectName)
        {
            var tbdFilePath = GetTbdFilePath(bucketName, objectName);

            if (File.Exists(tbdFilePath))
                throw new InvalidOperationException($"Cannot find '{objectName}' in bucket '{bucketName}'");
        }

        private void ClearFileTbdFlag(string bucketName, string objectName)
        {
            var tbdFilePath = GetTbdFilePath(bucketName, objectName);

            Policy.Handle<IOException>().Retry(10,
                (exception, i) => LogInformation($"Retrying to clear TBD flag ({i}): {exception.Message}")
            ).Execute(() =>
            {
                if (File.Exists(tbdFilePath))
                    File.Delete(tbdFilePath);
            });
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

        private string GetTbdFilePath(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName, TbdFolderName, objectName));
        }

        private string GetPendingFilePath(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(_settings.CachePath, bucketName, PendingFolderName, objectName));
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