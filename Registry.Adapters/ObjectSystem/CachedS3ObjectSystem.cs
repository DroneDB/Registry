using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Minio.DataModel;
using Minio.Exceptions;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;
using CopyConditions = Registry.Ports.ObjectSystem.Model.CopyConditions;

namespace Registry.Adapters.ObjectSystem
{
    public class CachedS3ObjectSystem : IObjectSystem
    {
        [JsonProperty("settings")]
        private readonly CachedS3ObjectSystemSettings _settings;
        private readonly ILogger<CachedS3ObjectSystem> _logger;
        private IObjectSystem _remoteStorage;

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

        #region Ctor

        [JsonConstructor]
        private CachedS3ObjectSystem()
        {
            LogInformation = s => Debug.WriteLine(s);
            LogError = (exception, s) => Debug.WriteLine($"{s} -> {exception}");
        }

        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            _remoteStorage = new S3ObjectSystem(_settings);            
        }
        
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
                else
                {
                    LogInformation("Cache HIT: The file is in use, we wait for it");

                    await using var descriptorStream =
                        await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Read,
                            FileShare.Read, FileRetriesDelay, FileRetries);

                    if (descriptorStream == null)
                        throw new InvalidOperationException("Cannot lock local file, timeout reached");

                    await using var fileStream =
                        await CommonUtils.WaitForFile(cachedFilePath, FileMode.OpenOrCreate, FileAccess.Read,
                            FileShare.Read, FileRetriesDelay, FileRetries);

                    if (cachedFilePath == null)
                        throw new InvalidOperationException("Cannot lock local file, timeout reached");

                    LogInformation("File is no longer in use, running callback");

                    callback(fileStream);
                }
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
                else
                {
                    LogInformation("Cache HIT: The file is in use, we wait for it");

                    await using var descriptorStream =
                        await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Read,
                            FileShare.Read, FileRetriesDelay, FileRetries);

                    if (descriptorStream == null)
                        throw new InvalidOperationException("Cannot lock local file, timeout reached");

                    LogInformation("File is no longer in use, copying requested file");
                    File.Copy(cachedFilePath, filePath);
                }
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
        
#pragma warning disable 1998
        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName,
#pragma warning restore 1998
            IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            var pendingFilePath = GetPendingFilePath(bucketName, objectName);
            
            try
            {
                EnsurePathExists(cachedFilePath);
                EnsurePathExists(descriptorFilePath);
                EnsurePathExists(pendingFilePath);

                LogInformation($"Waiting for descriptor '{descriptorFilePath}' to become available");

                await using var descriptorStream =
                    await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None,
                        FileRetriesDelay, FileRetries);

                using var descriptorReader = new StreamReader(descriptorStream);
                var obj = JsonConvert.DeserializeObject<ObjectDescriptor>(await descriptorReader.ReadToEndAsync()) ??
                          new ObjectDescriptor();

                if (obj.Stat != null)
                    return new ObjectInfo(objectName, obj.Stat.Size, obj.Stat.LastModified, obj.Stat.ETag,
                        obj.Stat.ContentType, obj.Stat.MetaData);

                ObjectInfo stat;

                if (File.Exists(pendingFilePath))
                {
                    // The file is pending, we have to generate a temporary stat
                    var res = AdaptersUtils.GenerateObjectInfo(cachedFilePath, objectName);

                    stat = new ObjectInfo(objectName, res.Size, res.LastModified, res.ETag, res.ContentType,
                        res.MetaData);
                }
                else
                {
                    // Let's call the actual function
                    stat = await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                        (exception, i) => LogInformation($"Retrying S3 stat ({i}): {exception.Message}")
                    ).ExecuteAsync(async () =>
                        await _remoteStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken));
                }

                obj.Stat = new ObjectInfoDto
                {
                    Name = stat.ObjectName,
                    Size = stat.Size,
                    ContentType = stat.ContentType,
                    ETag = stat.ETag,
                    LastModified = stat.LastModified,
                    MetaData = stat.MetaData != null ? new Dictionary<string, string>(stat.MetaData) : null
                };

                descriptorStream.Reset();

                await using var descriptorWriter = new StreamWriter(descriptorStream);
                await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

                return stat;

            }
            catch (MinioException ex)
            {
                LogError(ex, "Cannot call remote");
            }
            catch (IOException ex)
            {
                LogError(ex, "Cannot write descriptor");
            }

            return await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                (exception, i) => LogInformation($"Retrying S3 stat ({i}): {exception.Message}")
            ).ExecuteAsync(async () =>
                await _remoteStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken));

        }

        #endregion

        #region PutObject

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size,
            string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            LogInformation($"In PutObjectAsync('{bucketName}', '{objectName}', [Stream], {size}, '{contentType}')");

            HandleBucketToBeDeleted(bucketName);

            var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
            var cachedFilePath = GetCachedFilePath(bucketName, objectName);
            var pendingFilePath = GetPendingFilePath(bucketName, objectName);

            EnsurePathExists(descriptorFilePath);
            EnsurePathExists(cachedFilePath);
            EnsurePathExists(pendingFilePath);

            LogInformation($"Waiting for descriptor lock '{descriptorFilePath}'");

            await using var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);
            if (descriptorStream == null)
                throw new InvalidOperationException("Cannot lock local descriptor file, timeout reached");

            LogInformation($"Descriptor lock '{descriptorFilePath}' acquired");

            await using var fileStream =
                await CommonUtils.WaitForFile(cachedFilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries);

            if (fileStream == null)
                throw new InvalidOperationException("Cannot lock local file, timeout reached");

            LogInformation($"File lock '{cachedFilePath}' acquired");

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

            LogInformation($"Writing new descriptor");

            await using var descriptorWriter = new StreamWriter(descriptorStream);
            await descriptorWriter.WriteAsync(JsonConvert.SerializeObject(obj));

            LogInformation($"Copying data to file stream");

            // Verificare perchè non tiene in cache il file di cui abbiamo fatto la put

            // TODO: We are ignoring the lenght parameter, this could lead to problems if length != stream.Length
            await data.CopyToAsync(fileStream, cancellationToken);

            LogInformation($"Writing pending file '{pendingFilePath}'");

            await File.WriteAllTextAsync(pendingFilePath, DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"),
                cancellationToken);
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
            await File.WriteAllTextAsync(pendingFilePath, DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"),
                cancellationToken);
        }

        #endregion

        #region Remove

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

            await using (var descriptorStream =
                await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.Write,
                    FileShare.None, FileRetriesDelay, FileRetries))
            {
                descriptorStream.SetLength(0);

                if (descriptorStream == null)
                    throw new InvalidOperationException("Cannot lock local file, timeout reached");

                // Delete cached file
                CommonUtils.SafeDelete(cachedFilePath);

                // Mark it for deletion
                await File.WriteAllTextAsync(tbdFilePath, $"{bucketName}/${objectName}", cancellationToken);

            }

            if (!CommonUtils.SafeDelete(descriptorFilePath))
            {
                LogInformation($"Cannot remove descriptor '{descriptorFilePath}'");
            }
        }

        public Task RemoveObjectsAsync(string bucketName, string[] objectsNames,
            CancellationToken cancellationToken = default)
        {
            HandleBucketToBeDeleted(bucketName);

            return Task.WhenAll(
                objectsNames.Distinct().Select(objectName =>
                    RemoveObjectAsync(bucketName, objectName, cancellationToken)));
        }

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

        #endregion

        #region Sync

        public async Task SyncBucket(string bucketName)
        {
            LogInformation($"In SyncBucket('{bucketName}')");

            var bucketFolderPath = GetBucketFolder(bucketName);
            var bucketLockFilePath = Path.Combine(bucketFolderPath, LockFileName);
            var tbdLockFilePath = GetBucketTbdFilePath(bucketName);

            Directory.CreateDirectory(bucketFolderPath);
            EnsurePathExists(bucketLockFilePath);
            EnsurePathExists(tbdLockFilePath);

            try
            {
                LogInformation($"Waiting for bucket lock file '{bucketLockFilePath}'");

                await using (var bucketLockFileStream = new FileStream(bucketLockFilePath, FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await using var writer = new StreamWriter(bucketLockFileStream);
                    await writer.WriteAsync(DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));

                    LogInformation($"Acquired log for bucket '{bucketName}'");

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

            Directory.CreateDirectory(pendingFolderPath);

            var pendingFiles = Directory.EnumerateFiles(pendingFolderPath, "*", SearchOption.AllDirectories);

            foreach (var pendingFilePath in pendingFiles)
            {
                var objectName = Path.GetRelativePath(pendingFolderPath, pendingFilePath);
                var objectFilePath = GetCachedFilePath(bucketName, objectName);
                var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);

                LogInformation($"Synchronizing pending object '{objectName}', waiting for lock");

                await using var descriptorStream =
                    await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None, FileRetriesDelay, FileRetries);

                if (descriptorStream == null)
                    throw new InvalidOperationException("Cannot lock local file, timeout reached");

                LogInformation($"Lock acquired for '{descriptorFilePath}'");

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

                    LogInformation($"'{objectName}' needs to be syncronized");

                    try
                    {
                        // Attempt PutObject call
                        await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                            (exception, i) => LogInformation($"Retrying S3 object upload ({i}): {exception.Message}")
                        ).ExecuteAsync(async () =>
                            await _remoteStorage.PutObjectAsync(bucketName, objectName, objectFilePath,
                                descriptor.Info.ContentType, descriptor.Info.MetaData, descriptor.Info.SSE, default));

                        LogInformation($"Syncronized '{objectName}'");

                        // Clear descriptor file
                        descriptorStream.SetLength(0);

                        // Update sync time and clear last error
                        descriptor.SyncTime = DateTime.Now;
                        descriptor.LastError = null;

                        // Write down descriptor file
                        await using var writer = new StreamWriter(descriptorStream);
                        await writer.WriteAsync(JsonConvert.SerializeObject(descriptor));

                        LogInformation($"Deleting '{pendingFilePath}'");
                        if (!CommonUtils.SafeDelete(pendingFilePath))
                        {
                            LogInformation($"Cannot delete '{pendingFilePath}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, $"Cannot sync file '{pendingFilePath}'");

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
            var bucketFolderPath = GetBucketFolder(bucketName);
            var tbdFolderPath = Path.Combine(bucketFolderPath, TbdFolderName);

            Directory.CreateDirectory(tbdFolderPath);

            var tbdFiles = Directory.EnumerateFiles(tbdFolderPath, "*", SearchOption.AllDirectories).ToArray();

            if (!tbdFiles.Any())
            {
                LogInformation($"No tbd files are present");
                return;
            }

            var tbdRemoteList = new List<string>();

            foreach (var tbdFile in tbdFiles)
            {
                var objectName = Path.GetRelativePath(tbdFolderPath, tbdFile);
                var objectFilePath = GetCachedFilePath(bucketName, objectName);
                var descriptorFilePath = GetDescriptorFilePath(bucketName, objectName);
                var pendingFilePath = GetPendingFilePath(bucketName, objectName);

                LogInformation($"Deleting object '{objectName}'");

                try
                {
                    // // Open file descriptor
                    // await using (var descriptorStream =
                    //     await CommonUtils.WaitForFile(descriptorFilePath, FileMode.Open, FileAccess.ReadWrite,
                    //         FileShare.None, FileRetriesDelay, FileRetries))
                    // {
                    //     if (descriptorStream == null)
                    //         throw new InvalidOperationException("Cannot lock local file, timeout reached");
                    //

                    // Deserialize descriptor object from stream
                    //using (var reader = new StreamReader(descriptorStream, leaveOpen: true))
                    //    descriptor = JsonConvert.DeserializeObject<ObjectDescriptor>(await reader.ReadToEndAsync());

                    //if (descriptor?.Info == null)
                    //    throw new InvalidOperationException(
                    //        $"No upload info, invalid descriptor: '{descriptorFilePath}'");


                    LogInformation($"Deleting object file: '{objectFilePath}'");

                    if (!CommonUtils.SafeDelete(objectFilePath))
                        LogInformation($"Cannot delete object file '{objectFilePath}'");
                    else
                    {
                        LogInformation($"Deleted object file '{objectName}'");
                        
                        tbdRemoteList.Add(objectName);
                      
                        if (!CommonUtils.SafeDelete(descriptorFilePath))
                            LogInformation($"Cannot delete descriptor '{descriptorFilePath}'");

                        if (!CommonUtils.SafeDelete(pendingFilePath))
                            LogInformation($"Cannot delete pending '{pendingFilePath}'");
                        
                    }

                }
                catch (Exception ex)
                {
                    LogError(ex, $"An error occurred during sync '{descriptorFilePath}'");
                }
            }

            if (!tbdRemoteList.Any())
            {
                LogInformation($"Remote tdb list is empty, nothing to do");
                return;
            }

            try
            {
                // Attempt Delete all call
                await Policy.Handle<Exception>().RetryAsync(RemoteCallRetries,
                    (exception, i) =>
                        LogInformation($"Retrying S3 object delete ({i}): {exception.Message}")
                ).ExecuteAsync(async () =>
                    await _remoteStorage.RemoveObjectsAsync(bucketName, tbdRemoteList.ToArray(), default));

                var tbd = tbdRemoteList.Select(item => GetTbdFilePath(bucketName, item));

                // Let's remove all the tdb signal files
                foreach (var path in tbd)
                {
                    if (!CommonUtils.SafeDelete(path))
                        LogInformation($"Cannot remove tdb file '{path}'");
                }
            }
            catch (Exception ex)
            {
                LogError(ex, $"An error occurred during delete all");
            }
        }

        public async Task Sync()
        {
            try
            {
                LogInformation($"Waiting for global lock");

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
                        var bucketName = Path.GetRelativePath(_settings.CachePath, bucket);
                        LogInformation($"Synchronizing bucket '{bucketName}'");
                        await SyncBucket(bucketName);
                    }
                }

                if (!CommonUtils.SafeDelete(_globalLockFilePath))
                    throw new InvalidOperationException("Cannot delete global lock, this is bad");

                LogInformation("Released global lock, sync OK");
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Cannot run multiple synchronizations at once", ex);
            }
        }

        #endregion

        #region Proxied

        public Task GetObjectAsync(string bucketName, string objectName, long offset, long length,
            Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetObjectAsync(bucketName, objectName, offset, length, cb, sse,
                cancellationToken);
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

        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null,
            bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListObjectsAsync(bucketName, prefix, recursive, cancellationToken);
        }

        public Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return _remoteStorage.GetPolicyAsync(bucketName, cancellationToken);
        }

        public Task SetPolicyAsync(string bucketName, string policyJson,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

        #endregion

        #region Misc

        public StorageInfo GetStorageInfo()
        {
            return _remoteStorage.GetStorageInfo();
        }

        private class ObjectCarrier
        {
            public string FullPath { get; }
            public string ObjectName { get; }
            public string BucketName { get; }
            public long Size { get; }
            public DateTime CreationDate { get; }

            public ObjectCarrier(string fullPath, string objectName, string bucketName, long size,
                DateTime creationDate)
            {
                FullPath = fullPath;
                ObjectName = objectName;
                BucketName = bucketName;
                Size = size;
                CreationDate = creationDate;
            }
        }

        public async Task Cleanup()
        {
            await _remoteStorage.Cleanup();

            try
            {
                if (_settings.MaxSize == null)
                {
                    LogInformation($"Cache max size not set, cleanup ok");
                    return;
                }

                var maxSize = _settings.MaxSize.Value;

                var allFiles = (from filePath in
                            Directory.EnumerateFiles(_settings.CachePath, "*", SearchOption.AllDirectories)
                        let relPath = Path.GetRelativePath(_settings.CachePath, filePath).Replace('\\', '/')
                        let segments = relPath.Split('/')
                        where segments.Length > 2 && segments[1] == DescriptorsFolderName
                        let info = new FileInfo(filePath)
                        select new ObjectCarrier(
                            filePath,
                            // Python eat this
                            relPath[(segments[0].Length + segments[1].Length + 2)..][0..^5],
                            segments[0],
                            info.Length,
                            info.LastWriteTime)
                    ).ToArray();

                var tbds = new List<ObjectCarrier>();
                var totalCacheSize = allFiles.Sum(file => file.Size);
                var usage = (double)totalCacheSize / maxSize;

                LogInformation($"MaxCacheSize = {CommonUtils.GetBytesReadable(maxSize)}");
                LogInformation($"TotalCacheSize = {CommonUtils.GetBytesReadable(totalCacheSize)}");
                LogInformation($"Usage = {usage:P2}");

                if (totalCacheSize < maxSize)
                {
                    LogInformation($"No need to cleanup cache");
                    return;
                }

                long cacheExcess = totalCacheSize - maxSize;
                LogInformation($"CacheExcess = {CommonUtils.GetBytesReadable(cacheExcess)}");

                long targetFreeSpace = 0;

                if (_settings.CacheExpiration != null)
                {
                    LogInformation($"Checking expired files");

                    var expr = DateTime.Now - _settings.CacheExpiration;
                    tbds.AddRange(allFiles.Where(file => file.CreationDate > expr));

                    targetFreeSpace = tbds.Sum(file => file.Size);
                }

                if (targetFreeSpace < cacheExcess)
                {
                    allFiles = allFiles.OrderBy(item => item.CreationDate).ToArray();

                    foreach (var file in allFiles)
                    {
                        tbds.Add(file);
                        targetFreeSpace += file.Size;

                        if (targetFreeSpace > cacheExcess) break;
                    }
                }

                // Let's group by bucket in order to minimize the actual remote calls
                var tbdsGrouped = (from file in tbds
                    group file by file.BucketName
                    into grp
                    select new
                    {
                        BucketName = grp.Key,
                        ObjectNames = grp.Select(item => item.ObjectName).ToArray()
                    }).ToArray();

                // We could parallelize this
                foreach (var file in tbdsGrouped)
                {
                    await RemoveObjectsAsync(file.BucketName, file.ObjectNames);
                }
            }
            finally
            {
                await Sync();
            }
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

            if (!File.Exists(tbdFilePath)) return;

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
            
            public ObjectInfoDto Stat { get; set; }

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