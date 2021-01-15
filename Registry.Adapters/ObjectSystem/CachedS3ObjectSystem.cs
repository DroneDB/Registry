using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;
using Exception = System.Exception;

namespace Registry.Adapters.ObjectSystem
{
    public class CachedS3ObjectSystem : IObjectSystem
    {
        private readonly ILogger<CachedS3ObjectSystem> _logger;
        private readonly S3ObjectSystem _remoteStorage;

        public string CachePath { get; }
        public TimeSpan? CacheExpiration { get; }
        public long? MaxSize { get; }

        private long _currentCacheSize;

        private Dictionary<string, FileInfo> _fileInfos;

        private static object _sync = new();

        //private readonly Mutex evt;

        private string GetCacheFileName(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(CachePath, bucketName, objectName.Replace('/', '-')));
        }
        private string GetCacheFileInfoName(string bucketName, string objectName)
        {
            return Path.GetFullPath(Path.Combine(CachePath, bucketName, objectName.Replace('/', '-') + ".json"));
        }

        public CachedS3ObjectSystem(
            CachedS3ObjectSystemSettings settings, ILogger<CachedS3ObjectSystem> logger)

        {
            _logger = logger;
            CachePath = Path.GetFullPath(settings.CachePath);
            CacheExpiration = settings.CacheExpiration;
            MaxSize = settings.MaxSize;

            Directory.CreateDirectory(CachePath);

            UpdateCurrentCacheSize();
            TrimExcessCache();

            _remoteStorage = new S3ObjectSystem(settings);
        }

        #region Utils

        private void UpdateCurrentCacheSize()
        {
            Cleanup();
            _fileInfos = Directory.EnumerateFiles(CachePath, "*", SearchOption.AllDirectories).Select(file => new FileInfo(file))
                .ToDictionary(info => info.FullName, info => info);
            _currentCacheSize = _fileInfos.Sum(file => file.Value.Length);
        }

        public void Cleanup()
        {
            CleanupFolder(CachePath);

            CommonUtils.RemoveEmptyFolders(CachePath);
        }

        public void CleanupBucket(string bucketName)
        {
            var bucketFolder = GetBucketFolder(bucketName);
            if (Directory.Exists(bucketFolder))
                CleanupFolder(bucketFolder);
        }

        private void TrimExcessCache()
        {
            if (MaxSize == null)
            {
#if DEBUG
                _logger.LogInformation("No limitations in cache size");
#endif

                return;
            }

            lock (_sync)
            {

                if (_currentCacheSize < MaxSize)
                {
                    var perc = (double)_currentCacheSize / MaxSize;
#if DEBUG
                    _logger.LogInformation($"Total cache usage is {_currentCacheSize / 1024:F2}KB ({perc:P})");
#endif
                    return;

                }

                var spaceToFree = _currentCacheSize - MaxSize;

                _logger.LogInformation($"Freeing at least {spaceToFree / 1024:F2}KB");

                var files = _fileInfos.OrderBy(item => item.Value.LastAccessTime);

                long size = 0;
                int cnt = 0;

                foreach (var pair in files)
                {
                    var info = pair.Value;

                    if (info.Exists)
                    {

                        try
                        {
                            _logger.LogInformation($"Deleting '{pair.Key}'");

                            var fileSize = info.Length;
                            info.Delete();

                            _fileInfos.Remove(pair.Key);

                            size += fileSize;
                            cnt++;

                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Cannot delete file '{pair.Value}'");
                        }
                    }

                    if (size > spaceToFree)
                    {
                        _logger.LogInformation($"Deleted {cnt} files of size {size / 1024:F2}KB");
                        break;
                    }

                }

                _currentCacheSize -= size;
            }

        }

        private void CleanupFolder(string folder)
        {

            if (CacheExpiration == null)
            {
#if DEBUG
                _logger.LogInformation($"Cannot clean up folder '{folder}' because no cache expiration set");
#endif
                return;
            }

            lock(_sync) { 

                _logger.LogInformation($"Cleaning up folder '{folder}'");

                var expiredFiles = (from file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                                    let info = new FileInfo(file)
                                    where info.LastAccessTime + CacheExpiration < DateTime.Now
                                    select new { Path = file, Size = info.Length }).ToArray();

                if (!expiredFiles.Any())
                {
                    _logger.LogInformation("No expired files");
                    return;
                }

                int deletedFiles = 0;
                long totalDeletedFileSize = 0;

                foreach (var file in expiredFiles)
                {
                    if (File.Exists(file.Path))
                    {
                        try
                        {
#if DEBUG
                            _logger.LogInformation($"Deleting expired file '{file.Path}'");
#endif
                            File.Delete(file.Path);
                            deletedFiles++;
                            totalDeletedFileSize += file.Size;

                            // Safe for first execution
                            _fileInfos?.Remove(file.Path);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Cannot delete expired file '{file}'");
                        }
                    }
                }

                // Safe for first execution
                if (_fileInfos != null)
                    _currentCacheSize = _fileInfos.Sum(f => f.Value.Length);

                _logger.LogInformation($"Deleted {deletedFiles} files of {totalDeletedFileSize / 1024:F2}KB size");
            }
        }



        private void TrackCachedFile(string file)
        {
            var info = new FileInfo(file);

            if (!info.Exists)
            {
                _logger.LogWarning($"Trying to track non-existant file '{file}'");
                return;
            }

            if (_fileInfos.ContainsKey(file))
                _fileInfos[file] = info;
            else
            {
                _fileInfos.Add(file, info);
            }

            // This method can be optimized
            _currentCacheSize = _fileInfos.Sum(f => f.Value.Length);

        }

        private void EnsureBucketPathExists(string bucketName)
        {
            var bucketPath = GetBucketFolder(bucketName);
            Directory.CreateDirectory(bucketPath);

        }

        private void DetachCachedFile(string file)
        {
            if (_fileInfos.Remove(file))
            {
#if DEBUG
                _logger.LogInformation($"Detached file '{file}' from cache");
#endif
                _currentCacheSize = _fileInfos.Sum(f => f.Value.Length);
            };
        }
        private string GetBucketFolder(string bucketName)
        {
            return Path.GetFullPath(Path.Combine(CachePath, bucketName));
        }

        #endregion

        #region Get

        public async Task GetObjectAsync(string bucketName, string objectName, Action<Stream> callback, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {

            CleanupBucket(bucketName);

            var cachedFileName = GetCacheFileName(bucketName, objectName);

            try
            {
                var info = new FileInfo(cachedFileName);
                if (info.Exists)
                {

                    var memory = new MemoryStream();
                    await using (var s = info.OpenRead())
                        await s.CopyToAsync(memory, cancellationToken);

                    memory.Reset();

                    callback(memory);
                    return;

                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot read from cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            await _remoteStorage.GetObjectAsync(bucketName, objectName, stream =>
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);

                try
                {
                    EnsureBucketPathExists(bucketName);
                    memory.Reset();
                    using (var s = File.OpenWrite(cachedFileName)) memory.CopyTo(s);

                    TrackCachedFile(cachedFileName);
                    TrimExcessCache();

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Cannot write to cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
                }

                memory.Reset();

                callback(memory);
            }, sse, cancellationToken);

        }


        public async Task GetObjectAsync(string bucketName, string objectName, string filePath, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {

            CleanupBucket(bucketName);

            var cachedFileName = GetCacheFileName(bucketName, objectName);

            try
            {
                var info = new FileInfo(cachedFileName);
                if (info.Exists)
                {
                    info.CopyTo(filePath, true);
                    return;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot read from cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            await _remoteStorage.GetObjectAsync(bucketName, objectName, filePath, sse, cancellationToken);

            try
            {
                EnsureBucketPathExists(bucketName);

                File.Copy(filePath, cachedFileName, true);

                TrackCachedFile(cachedFileName);
                TrimExcessCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot copy to cache from '{filePath}' to '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

        }


        public async Task<ObjectInfo> GetObjectInfoAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            CleanupBucket(bucketName);

            var cachedFileInfoName = GetCacheFileInfoName(bucketName, objectName);

            try
            {
                if (File.Exists(cachedFileInfoName))
                {
                    return JsonConvert.DeserializeObject<ObjectInfo>(
                        await File.ReadAllTextAsync(cachedFileInfoName, cancellationToken));
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot read from cache the file info '{cachedFileInfoName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            var objectInfo = await _remoteStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);

            try
            {
                EnsureBucketPathExists(bucketName);

                await File.WriteAllTextAsync(cachedFileInfoName, JsonConvert.SerializeObject(objectInfo, Formatting.Indented), cancellationToken);

                TrackCachedFile(cachedFileInfoName);
                TrimExcessCache();

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot write file info to cache '{cachedFileInfoName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            return objectInfo;
        }

        #endregion

        #region Put

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            await _remoteStorage.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse, cancellationToken);

            var cachedFileName = GetCacheFileName(bucketName, objectName);

            try
            {
                data.Reset();

                EnsureBucketPathExists(bucketName);

                await using (var writer = File.OpenWrite(cachedFileName))
                    await data.CopyToAsync(writer, cancellationToken);

                TrackCachedFile(cachedFileName);
                TrimExcessCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot write to cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            await _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse, cancellationToken);

            var cachedFileName = GetCacheFileName(bucketName, objectName);
            try
            {
                EnsureBucketPathExists(bucketName);

                File.Copy(filePath, cachedFileName, true);

                TrackCachedFile(cachedFileName);
                TrimExcessCache();

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Cannot copy to cache from '{filePath}' to '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

        }

        #endregion


        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {

            CleanupBucket(bucketName);

            var cachedFileName = GetCacheFileName(bucketName, objectName);

            try
            {
                if (File.Exists(cachedFileName))
                {
                    File.Delete(cachedFileName);
                    DetachCachedFile(cachedFileName);

                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"Cannot delete cached file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            await _remoteStorage.RemoveObjectAsync(bucketName, objectName, cancellationToken);

        }


        public async Task RemoveBucketAsync(string bucketName, bool force = true, CancellationToken cancellationToken = default)
        {

            var bucketFolder = GetBucketFolder(bucketName);
            try
            {
                if (Directory.Exists(bucketFolder))
                {
                    var files = Directory.EnumerateFiles(bucketFolder, "*", SearchOption.AllDirectories);
                    foreach (var file in files)
                        DetachCachedFile(file);

                    Directory.Delete(bucketFolder, true);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"Cannot delete cached files in folder '{bucketFolder}' of '{bucketName}' bucket and");
            }

            await _remoteStorage.RemoveBucketAsync(bucketName, force, cancellationToken);

        }

        #region Proxied

        public async Task GetObjectAsync(string bucketName, string objectName, long offset, long length, Action<Stream> cb,
            IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {
            // NOTE: Not supported partial file recovery
            await _remoteStorage.GetObjectAsync(bucketName, objectName, offset, length, cb, sse, cancellationToken);
        }


        public IObservable<ObjectUpload> ListIncompleteUploads(string bucketName, string prefix = "", bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListIncompleteUploads(bucketName, prefix, recursive, cancellationToken);
        }

        public async Task RemoveIncompleteUploadAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {
            await _remoteStorage.RemoveIncompleteUploadAsync(bucketName, objectName, cancellationToken);
        }

        public async Task CopyObjectAsync(string bucketName, string objectName, string destBucketName, string destObjectName = null,
            IReadOnlyDictionary<string, string> copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
            IServerEncryption sseDest = null, CancellationToken cancellationToken = default)
        {
            await _remoteStorage.CopyObjectAsync(bucketName, objectName, destBucketName, destObjectName, copyConditions, metadata, sseSrc, sseDest, cancellationToken);
        }

        public async Task MakeBucketAsync(string bucketName, string location = null, CancellationToken cancellationToken = default)
        {
            await _remoteStorage.MakeBucketAsync(bucketName, location, cancellationToken);
        }

        public async Task<ListBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
        {
            return await _remoteStorage.ListBucketsAsync(cancellationToken);
        }
        public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _remoteStorage.BucketExistsAsync(bucketName, cancellationToken);
        }
        public IObservable<ItemInfo> ListObjectsAsync(string bucketName, string prefix = null, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return _remoteStorage.ListObjectsAsync(bucketName, prefix, recursive, cancellationToken);
        }

        public async Task<string> GetPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            return await _remoteStorage.GetPolicyAsync(bucketName, cancellationToken);
        }

        public async Task SetPolicyAsync(string bucketName, string policyJson,
            CancellationToken cancellationToken = default)
        {
            await _remoteStorage.SetPolicyAsync(bucketName, policyJson, cancellationToken);
        }

        public StorageInfo GetStorageInfo()
        {
            return _remoteStorage.GetStorageInfo();
        }

        #endregion
        
    }
}
