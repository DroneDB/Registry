using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Minio.Exceptions;
using Newtonsoft.Json;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.ObjectSystem;
using Registry.Ports.ObjectSystem.Model;
using CopyConditions = Registry.Ports.ObjectSystem.Model.CopyConditions;
using Exception = System.Exception;

namespace Registry.Adapters.ObjectSystem
{
    public class CachedS3ObjectSystem : IObjectSystem
    {
        [JsonProperty("settings")]
        private readonly CachedS3ObjectSystemSettings _settings;
        private readonly ILogger<CachedS3ObjectSystem> _logger;
        private S3ObjectSystem _remoteStorage;

        private const int MaxUploadAttempts = 5;

        [JsonIgnore]
        public string CachePath { get; private set; }
        [JsonIgnore]
        public TimeSpan? CacheExpiration { get; private set; }
        [JsonIgnore]
        public long? MaxSize { get; private set; }

        private long _currentCacheSize;

        private Dictionary<string, FileInfo> _fileInfos;

        // NOTE: This is thread safe as long that there is only one worker process
        private static readonly object _sync = new object();

        private const string SignalFileSuffix = "-pending";
        private const string BrokenFileSuffix = "-broken";

        private string GetCacheFileName(string bucketName, string objectName)
        {
            var path = Path.GetFullPath(Path.Combine(CachePath, bucketName, objectName));

            // Create folder tree if not existing
            var folder = Path.GetDirectoryName(path);
            if (folder != null) Directory.CreateDirectory(folder);

            return path;
        }
        private string GetCacheFileInfoName(string bucketName, string objectName)
        {
            return GetCacheFileName(bucketName, objectName) + ".json";
        }

        private ObjectInfoDto GetFileObjectInfo(string path)
        {
            return !File.Exists(path) ? null : JsonConvert.DeserializeObject<ObjectInfoDto>(File.ReadAllText(path, Encoding.UTF8));
        }

        private static string GetCacheFileInfoName(string cacheFile)
        {
            return cacheFile + ".json";
        }

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
            CachePath = Path.GetFullPath(_settings.CachePath);
            CacheExpiration = _settings.CacheExpiration;
            MaxSize = _settings.MaxSize;
            UpdateCurrentCacheSize();
        }

        public CachedS3ObjectSystem(CachedS3ObjectSystemSettings settings, ILogger<CachedS3ObjectSystem> logger)
        {
            _settings = settings;
            _logger = logger;
            LogInformation = s => _logger.LogInformation(s);
            LogError = (ex, s) => _logger.LogError(ex, s);

            CachePath = Path.GetFullPath(settings.CachePath);
            CacheExpiration = settings.CacheExpiration;
            MaxSize = settings.MaxSize;

            Directory.CreateDirectory(CachePath);

            UpdateCurrentCacheSize();
            TrimExcessCache();

            _remoteStorage = new S3ObjectSystem(settings);
        }

        #region Utils

        private readonly Action<string> LogInformation;
        private readonly Action<Exception, string> LogError;

        private void TrimExcessCache()
        {
            // Check if we need to enforce cache size (MaxSize == 0 is a failsafe)
            if (MaxSize == null || MaxSize == 0)
            {
#if DEBUG
                LogInformation("No limitations in cache size");
#endif

                return;
            }

            lock (_sync)
            {

                if (_currentCacheSize < MaxSize)
                {
                    var perc = (double)_currentCacheSize / MaxSize;
#if DEBUG
                    LogInformation($"Total cache usage is {_currentCacheSize / 1024:F2}KB ({perc:P})");
#endif
                    return;

                }

                var spaceToFree = _currentCacheSize - MaxSize;

                LogInformation($"Freeing at least {spaceToFree / 1024:F2}KB");

                var files = _fileInfos.OrderBy(item => item.Value.LastAccessTime);

                long size = 0;
                int cnt = 0;

                foreach (var pair in files)
                {
                    var info = pair.Value;

                    if (info.Exists)
                    {

                        var signalFileExists = File.Exists(GetSignalFileName(info.FullName));
                        var brokenFileExists = File.Exists(GetBrokenFileName(info.FullName));

                        if (!signalFileExists && !brokenFileExists)
                        {
                            try
                            {
                                LogInformation($"Deleting '{pair.Key}'");

                                var fileSize = info.Length;
                                info.Delete();

                                _fileInfos.Remove(pair.Key);

                                size += fileSize;
                                cnt++;
                            }
                            catch (Exception ex)
                            {
                                LogError(ex, $"Cannot delete file '{pair.Value}'");
                            }
                        }
                        else
                        {
                            LogInformation($"Skipping '{pair.Key}' because is being uploaded");
                        }
                    }

                    if (size > spaceToFree)
                    {
                        LogInformation($"Deleted {cnt} files of size {size / 1024:F2}KB");
                        break;
                    }

                }

                _currentCacheSize -= size;
            }

        }

        private void CleanupFolder(string folder)
        {

            // Check if we need to enforce cache expiration (default compare is a failsafe)

            if (CacheExpiration == null || CacheExpiration.Value == default)
            {
#if DEBUG
                LogInformation($"No need to clean up folder '{folder}' because no cache expiration set");
#endif
                return;
            }

            lock (_sync)
            {

                LogInformation($"Cleaning up folder '{folder}'");

                var expiredFiles = (from file in SafeGetAllFiles(folder)
                                    let info = new FileInfo(file)
                                    where info.LastAccessTime + CacheExpiration < DateTime.Now
                                    select new { Path = file, Size = info.Length }).ToArray();

                if (!expiredFiles.Any())
                {
                    LogInformation("No expired files");
                    return;
                }

                int deletedFiles = 0;
                long totalDeletedFileSize = 0;

                foreach (var file in expiredFiles)
                {
                    if (!File.Exists(file.Path)) continue;

                    // If we have a .json file that is an info file of a file not synced yet or that is being uploaded we skip it
                    if (file.Path.EndsWith(".json") && (File.Exists(file.Path.Replace(".json", BrokenFileSuffix)) ||
                                                        File.Exists(file.Path.Replace(".json", SignalFileSuffix))))
                    {
                        LogInformation($"Skipping '{file.Path}' because is info file of not synced yet file");
                        continue;
                    }

                    if (!File.Exists(GetSignalFileName(file.Path)) && !File.Exists(GetBrokenFileName(file.Path)))
                    {
                        try
                        {
#if DEBUG
                            LogInformation($"Deleting expired file '{file.Path}'");
#endif
                            File.Delete(file.Path);
                            deletedFiles++;
                            totalDeletedFileSize += file.Size;

                            // Safe for first execution
                            _fileInfos?.Remove(file.Path);
                        }
                        catch (Exception ex)
                        {
                            LogError(ex, $"Cannot delete expired file '{file}'");
                        }
                    }
                    else
                    {
                        LogInformation($"Skipping '{file.Path}' because it's being uploaded or is not synced yet");
                    }
                }

                // Safe for first execution
                if (_fileInfos != null)
                    _currentCacheSize = _fileInfos.Sum(f => f.Value.Length);

                LogInformation($"Deleted {deletedFiles} files of {totalDeletedFileSize / 1024:F2}KB size");
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

        private void UpdateCurrentCacheSize()
        {
            Cleanup();

            lock (_sync)
            {
                _fileInfos = SafeGetAllFiles(CachePath).Select(file => new FileInfo(file))
                    .ToDictionary(info => info.FullName, info => info);
                _currentCacheSize = _fileInfos.Sum(file => file.Value.Length);
            }
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


        private void EnsureBucketPathExists(string bucketName)
        {
            var bucketPath = GetBucketFolder(bucketName);
            Directory.CreateDirectory(bucketPath);

        }

        private void DetachCachedFile(string file)
        {
            if (!_fileInfos.Remove(file)) return;

#if DEBUG
            LogInformation($"Detached file '{file}' from cache");
#endif
            _currentCacheSize = _fileInfos.Sum(f => f.Value.Length);
        }

        private string GetBucketFolder(string bucketName)
        {
            return Path.GetFullPath(Path.Combine(CachePath, bucketName));
        }

        private static string GetSignalFileName(string path)
        {
            return path + SignalFileSuffix;
        }

        private string GetSignalFileName(string bucketName, string objectName)
        {
            return GetCacheFileName(bucketName, objectName) + SignalFileSuffix;
        }

        private static bool IsSignalFile(string file)
        {
            return file != null && file.EndsWith(SignalFileSuffix);
        }

        private static string GetBrokenFileName(string path)
        {
            return path + BrokenFileSuffix;
        }

        private string GetBrokenFileName(string bucketName, string objectName)
        {
            return GetCacheFileName(bucketName, objectName) + BrokenFileSuffix;
        }

        private static bool IsBrokenFile(string file)
        {
            return file != null && file.EndsWith(BrokenFileSuffix);
        }

        private static string[] SafeGetAllFiles(string path)
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(p => !IsSignalFile(p) && !IsBrokenFile(p)).ToArray();
        }

        private static async Task CreateInfoFile(string cachedFileName, string bucketName, string objectName, Dictionary<string, string> metaData, CancellationToken cancellationToken)
        {
            var info = AdaptersUtils.GenerateObjectInfo(cachedFileName, objectName);
            info.MetaData = metaData;

            await File.WriteAllTextAsync(GetCacheFileInfoName(cachedFileName),
                JsonConvert.SerializeObject(info, Formatting.Indented), cancellationToken);
        }

        private static async Task CreateSignalFile(string file)
        {
            await File.WriteAllTextAsync(GetSignalFileName(file), DateTime.Now.ToString("O"));
        }

        private static bool IsFileSignaled(string file)
        {
            return File.Exists(GetSignalFileName(file));
        }


        public SyncFilesRes SyncFiles()
        {

            var syncedFiles = new List<string>();
            var errorFiles = new List<SyncFileError>();

            LogInformation("Looking for unsyncronized files");

            lock (_sync)
            {
                var files = Directory.EnumerateFiles(CachePath, "*" + BrokenFileSuffix, SearchOption.AllDirectories).ToArray();

                if (!files.Any())
                {
                    LogInformation("No broken files are preset");
                    return new SyncFilesRes();
                }

                foreach (var f in files)
                {
                    var file = f.Substring(0, f.Length - BrokenFileSuffix.Length);

                    try
                    {
                        var bucketName = Path.GetRelativePath(CachePath, Path.GetDirectoryName(file) ?? string.Empty)
                            .Split(Path.DirectorySeparatorChar).FirstOrDefault();

                        if (bucketName == null)
                        {
                            var msg = $"Cannot retrieve bucket name from file '{file}'";
                            _logger.LogWarning(msg);
                            errorFiles.Add(new SyncFileError { ErrorMessage = msg, Path = file });

                            break;
                        }

                        var info = GetFileObjectInfo(file + ".json");
                        if (info == null)
                        {
                            var msg = $"Cannot get file info of '{file}'";
                            _logger.LogWarning(msg);
                            errorFiles.Add(new SyncFileError { ErrorMessage = msg, Path = file });

                            break;
                        }

                        LogInformation($"Syncing '{file}' to '{bucketName}'");

                        // This is like a nuclear bomb
                        _remoteStorage.PutObjectAsync(bucketName, info.Name, file, info.ContentType,
                                info.MetaData?.ToDictionary(key => key.Key, val => val.Value)).GetAwaiter().GetResult();

                        LogInformation("File is synced, deleting broken flag file and signal file");

                        CommonUtils.SafeDelete(GetBrokenFileName(file));
                        CommonUtils.SafeDelete(GetSignalFileName(file));

                        LogInformation("Deleted broken flag file");

                        syncedFiles.Add(file);

                    }
                    catch (Exception ex)
                    {
                        LogError(ex, $"Cannot sync '{file}'");
                        errorFiles.Add(new SyncFileError { ErrorMessage = ex.Message, Path = file });
                    }

                }

                return new SyncFilesRes
                {
                    ErrorFiles = errorFiles.ToArray(),
                    SyncedFiles = syncedFiles.ToArray()
                };
            }
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
                    callback(info.OpenRead());
                    return;
                }
            }
            catch (Exception ex)
            {
                LogError(ex, $"Cannot read from cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            await _remoteStorage.GetObjectAsync(bucketName, objectName, stream =>
            {
                try
                {
                    EnsureBucketPathExists(bucketName);
                    using (var s = File.OpenWrite(cachedFileName)) stream.CopyTo(s);

                    TrackCachedFile(cachedFileName);
                    TrimExcessCache();

                }
                catch (Exception ex)
                {
                    LogError(ex, $"Cannot write to cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
                    throw;
                }

                callback(File.OpenRead(cachedFileName));

            }, sse, cancellationToken);

            // Added to populate file info cache
            await GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);

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
                LogError(ex, $"Cannot read from cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
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
                LogError(ex, $"Cannot copy to cache from '{filePath}' to '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            // Added to populate file info cache
            await GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);


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
                LogError(ex, $"Cannot read from cache the file info '{cachedFileInfoName}' in '{bucketName}' bucket and '{objectName}' object");
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
                LogError(ex, $"Cannot write file info to cache '{cachedFileInfoName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            return objectInfo;
        }

        public async Task<bool> ObjectExistsAsync(string bucketName, string objectName, IServerEncryption sse = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _remoteStorage.GetObjectInfoAsync(bucketName, objectName, sse, cancellationToken);
                return true;
            }
            catch (MinioException e)
            {
                LogInformation($"Object '{objectName}' in bucket '{bucketName}' does not exist: {e}");
            }

            return false;
        }

        #endregion

        #region Put

        public async Task PutObjectAsync(string bucketName, string objectName, Stream data, long size, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            var cachedFileName = GetCacheFileName(bucketName, objectName);

            try
            {
                data.Reset();

                EnsureBucketPathExists(bucketName);

                if (File.Exists(cachedFileName)) File.Delete(cachedFileName);

                await using (var writer = File.OpenWrite(cachedFileName))
                    await data.CopyToAsync(writer, cancellationToken);

                await CreateInfoFile(cachedFileName, bucketName, objectName, metaData, cancellationToken);
                await CreateSignalFile(cachedFileName);

                TrackCachedFile(cachedFileName);
                TrimExcessCache();
            }
            catch (Exception ex)
            {
                LogError(ex, $"Cannot write to cache the file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
                throw;
            }

            await SafePutObjectAsync(bucketName, objectName, metaData,
                async () => await _remoteStorage.PutObjectAsync(bucketName, objectName, data, size, contentType, metaData, sse,
                    cancellationToken), cancellationToken);

        }

        public async Task PutObjectAsync(string bucketName, string objectName, string filePath, string contentType = null,
            Dictionary<string, string> metaData = null, IServerEncryption sse = null, CancellationToken cancellationToken = default)
        {

            var cachedFileName = GetCacheFileName(bucketName, objectName);

            if (!IsFileSignaled(cachedFileName))
                await CreateSignalFile(cachedFileName);

            try
            {
                EnsureBucketPathExists(bucketName);

                File.Copy(filePath, cachedFileName, true);

                await CreateInfoFile(cachedFileName, bucketName, objectName, metaData, cancellationToken);
                await CreateSignalFile(cachedFileName);

                TrackCachedFile(cachedFileName);
                TrimExcessCache();

            }
            catch (Exception ex)
            {
                LogError(ex, $"Cannot copy to cache from '{filePath}' to '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
            }

            await SafePutObjectAsync(bucketName, objectName, metaData,
                async () => await _remoteStorage.PutObjectAsync(bucketName, objectName, filePath, contentType, metaData, sse, cancellationToken), cancellationToken);

        }

        private async Task SafePutObjectAsync(string bucketName, string objectName, Dictionary<string, string> metaData, Func<Task> call,
            CancellationToken cancellationToken)
        {

            var signalFileName = GetSignalFileName(bucketName, objectName);
            var cachedFileName = GetCacheFileName(bucketName, objectName);

            if (!IsFileSignaled(cachedFileName))
                await CreateSignalFile(cachedFileName);

            int cnt = 1;
            do
            {
                try
                {
                    LogInformation($"Uploading file '{objectName}' to '{bucketName}' bucket");

                    await call();

                    break;
                }
                catch (Exception ex)
                {
                    LogError(ex, $"Cannot file '{objectName}' to '{bucketName}' bucket to S3 ({cnt}° attempt)");
                    cnt++;
                }

            } while (cnt < MaxUploadAttempts);

            if (cnt == MaxUploadAttempts)
            {
                LogError(new Exception(),
                    "No attempt to upload file to S3 was successful, leaving the file in unsyncronized state");

                // Signal that this file is not synced
                await File.WriteAllTextAsync(GetBrokenFileName(bucketName, objectName), DateTime.Now.ToString("O"), cancellationToken);

                // Remove pending file
                var signalFile = GetSignalFileName(bucketName, objectName);
                if (File.Exists(signalFile)) File.Delete(signalFile);

                // Write down call info
                await CreateInfoFile(cachedFileName, bucketName, objectName, metaData, cancellationToken);

                return;
            }

            LogInformation($"Removing signal file '{signalFileName}'");
            File.Delete(signalFileName);
        }



        #endregion

        #region Delete
        private bool RemoveLocalObject(string bucketName, string objectName)
        {
            var cachedFileName = GetCacheFileName(bucketName, objectName);

            try
            {
                if (File.Exists(cachedFileName))
                {
                    File.Delete(cachedFileName);
                    DetachCachedFile(cachedFileName);

                    // Remove pending file if exists
                    var signalFile = GetSignalFileName(bucketName, objectName);
                    if (File.Exists(signalFile)) File.Delete(signalFile);

                    var infoFile = GetCacheFileInfoName(cachedFileName);
                    if (File.Exists(infoFile)) File.Delete(infoFile);

                }
            }
            catch (Exception ex)
            {
                LogError(ex,
                    $"Cannot delete cached file '{cachedFileName}' in '{bucketName}' bucket and '{objectName}' object");
                return false;
            }

            return true;

        }
        public async Task RemoveObjectsAsync(string bucketName, string[] objectsNames, CancellationToken cancellationToken = default)
        {
            CleanupBucket(bucketName);

            foreach (var obj in objectsNames)
            {
                if (!RemoveLocalObject(bucketName, obj))
                {
                    _logger.LogWarning($"Cannot remove local object '{obj}' in bucket '{bucketName}'");
                }
            }

            await _remoteStorage.RemoveObjectsAsync(bucketName, objectsNames, cancellationToken);

        }
        
        public async Task RemoveObjectAsync(string bucketName, string objectName, CancellationToken cancellationToken = default)
        {

            CleanupBucket(bucketName);

            if (!RemoveLocalObject(bucketName, objectName))
            {
                _logger.LogWarning($"Cannot remove local object '{objectName}' in bucket '{bucketName}'");
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
                    var files = SafeGetAllFiles(bucketFolder);
                    foreach (var file in files)
                        DetachCachedFile(file);

                    Directory.Delete(bucketFolder, true);
                }

            }
            catch (Exception ex)
            {
                LogError(ex,
                    $"Cannot delete cached files in folder '{bucketFolder}' of '{bucketName}' bucket and");
            }

            await _remoteStorage.RemoveBucketAsync(bucketName, force, cancellationToken);

        }

        #endregion

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
            CopyConditions copyConditions = null, Dictionary<string, string> metadata = null, IServerEncryption sseSrc = null,
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
