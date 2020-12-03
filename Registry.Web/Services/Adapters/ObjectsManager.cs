using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Minio.Exceptions;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly IChunkedUploadManager _chunkedUploadManager;
        private readonly IDdbFactory _ddbFactory;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly IDistributedCache _distributedCache;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        private const string LocationKey = "location";

        // TODO: Could be moved to config
        private const int DefaultThumbnailSize = 512;

        private const string BucketNameFormat = "{0}-{1}";

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IObjectSystem objectSystem,
            IChunkedUploadManager chunkedUploadManager,
            IOptions<AppSettings> settings,
            IDdbFactory ddbFactory,
            IUtils utils, IAuthManager authManager, IDistributedCache distributedCache)
        {
            _logger = logger;
            _context = context;
            _objectSystem = objectSystem;
            _chunkedUploadManager = chunkedUploadManager;
            _ddbFactory = ddbFactory;
            _utils = utils;
            _authManager = authManager;
            _distributedCache = distributedCache;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path)
        {

            await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);

            _logger.LogInformation($"Searching in '{path}'");

            var files = ddb.Search(path).Select(file => file.ToDto()).ToArray();

            _logger.LogInformation($"Found {files.Length} objects");

            return files;
        }

        public async Task<ObjectRes> Get(string orgSlug, string dsSlug, string path)
        {

            await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path should not be null");

            return await InternalGet(orgSlug, dsSlug, path);
        }

        private async Task<ObjectRes> InternalGet(string orgSlug, string dsSlug, string path)
        {
            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);

            var res = ddb.Search(path).FirstOrDefault();

            if (res == null)
                throw new NotFoundException($"Cannot find '{path}'");

            var bucketName = string.Format(BucketNameFormat, orgSlug, dsSlug);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogInformation("Bucket does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetRegion());

                _logger.LogInformation("Bucket created");
            }

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, res.Path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{res.Path}' in storage provider");

            await using var memory = new MemoryStream();

            _logger.LogInformation($"Getting object '{res.Path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, res.Path, stream => stream.CopyTo(memory));

            return new ObjectRes
            {
                ContentType = objInfo.ContentType,
                Name = objInfo.ObjectName,
                Data = memory.ToArray(),
                // TODO: We can add more fields from DDB if we need them
                Type = res.Type,
                Hash = res.Hash,
                Size = res.Size
            };
        }

        private string SafeGetRegion()
        {
            if (_settings.StorageProvider.Type != StorageType.S3) return null;

            var settings = _settings.StorageProvider.Settings.ToObject<S3ProviderSettings>();
            if (settings == null)
            {
                _logger.LogWarning("No S3 settings loaded, shouldn't this be a problem?");
                return null;
            }

            if (settings.Region == null)
                _logger.LogWarning("No region specified in storage provider config");

            return settings.Region;
        }

        public async Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data)
        {
            await using var stream = new MemoryStream(data);
            stream.Reset();
            return await AddNew(orgSlug, dsSlug, path, stream);
        }

        public async Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var bucketName = string.Format(BucketNameFormat, orgSlug, dsSlug);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            if (!await _objectSystem.BucketExistsAsync(bucketName))
            {

                _logger.LogInformation($"Bucket '{bucketName}' does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetLocation());

                _logger.LogInformation("Bucket created");
            }

            // TODO: I highly doubt the robustness of this 
            var contentType = MimeTypes.GetMimeType(path);

            _logger.LogInformation($"Uploading '{path}' (size {stream.Length}) to bucket '{bucketName}'");

            // TODO: No metadata / encryption ?
            await _objectSystem.PutObjectAsync(bucketName, path, stream, stream.Length, contentType);

            _logger.LogInformation("File uploaded, adding to DDB");

            // Add to DDB
            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);
            ddb.Add(path, stream);

            _logger.LogInformation("Added to DDB");

            // Refresh objects count and total size
            dataset.UpdateStatistics(ddb);
            await _context.SaveChangesAsync();

            var obj = new UploadedObjectDto
            {
                Path = path,
                ContentType = contentType,
                Size = stream.Length
            };

            return obj;
        }

        private string SafeGetLocation()
        {
            if (_settings.StorageProvider.Type != StorageType.S3) return null;

            var settings = _settings.StorageProvider.Settings.ToObject<S3ProviderSettings>();
            if (settings == null)
            {
                _logger.LogWarning("No S3 settings loaded, shouldn't this be a problem?");
                return null;
            }

            if (settings.Location == null)
                _logger.LogWarning("No location specified in storage provider config");

            return settings.Location;
        }

        public async Task Delete(string orgSlug, string dsSlug, string path)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var bucketName = string.Format(BucketNameFormat, orgSlug, dsSlug);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            _logger.LogInformation($"Deleting '{path}'");

            await _objectSystem.RemoveObjectAsync(bucketName, path);

            _logger.LogInformation($"File deleted, removing from DDB");

            // Remove from DDB
            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);

            ddb.Remove(path);
            dataset.UpdateStatistics(ddb);

            // Refresh objects count and total size
            await _context.SaveChangesAsync();

            _logger.LogInformation("Removed from DDB");

        }

        public async Task DeleteAll(string orgSlug, string dsSlug)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DeleteAll('{orgSlug}/{dsSlug}')");

            var bucketName = string.Format(BucketNameFormat, orgSlug, dsSlug);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogWarning($"Asked to remove non-existing bucket '{bucketName}'");
                return;
            }

            _logger.LogInformation($"Deleting bucket");

            await _objectSystem.RemoveBucketAsync(bucketName);

            _logger.LogInformation($"Bucket deleted, removing all files from DDB ");

            // Remove all from DDB
            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);

            var res = ddb.Search(null);
            foreach (var item in res)
                ddb.Remove(item.Path);

            // Refresh objects count and total size
            dataset.UpdateStatistics(ddb);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Removed all from DDB");

            // TODO: Maybe it's more clever to remove the entire sqlite database instead of performing a per-file delete. Just my 2 cents

        }

        #region Sessions
        public async Task<int> AddNewSession(string orgSlug, string dsSlug, int chunks, long size)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var fileName = $"{orgSlug.ToSlug()}-{dsSlug.ToSlug()}-{CommonUtils.RandomString(16)}";

            _logger.LogDebug($"Generated '{fileName}' as temp file name");

            var sessionId = _chunkedUploadManager.InitSession(fileName, chunks, size);

            return sessionId;
        }

        public async Task AddToSession(string orgSlug, string dsSlug, int sessionId, int index, Stream stream)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            await _chunkedUploadManager.Upload(sessionId, stream, index);
        }

        public async Task AddToSession(string orgSlug, string dsSlug, int sessionId, int index, byte[] data)
        {
            await using var memory = new MemoryStream(data);
            memory.Reset();
            await AddToSession(orgSlug, dsSlug, sessionId, index, memory);
        }
        #endregion

        public async Task<UploadedObjectDto> CloseSession(string orgSlug, string dsSlug, int sessionId, string path)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var tempFilePath = _chunkedUploadManager.CloseSession(sessionId, false);

            UploadedObjectDto newObj;

            await using (var fileStream = File.OpenRead(tempFilePath))
            {
                newObj = await AddNew(orgSlug, dsSlug, path, fileStream);
            }

            _chunkedUploadManager.CleanupSession(sessionId);

            File.Delete(tempFilePath);

            return newObj;
        }

        public async Task MoveDataset(string orgSlug, string dsSlug, string newDsSlug)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var oldBucket = string.Format(BucketNameFormat, orgSlug, dsSlug);

            _logger.LogInformation($"Using bucket '{oldBucket}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(oldBucket);

            if (!bucketExists)
            {
                _logger.LogWarning($"Asked to remove non-existing bucket '{oldBucket}'");
                return;
            }

            _logger.LogInformation($"Renaming bucket");

            var newBucket = string.Format(BucketNameFormat, orgSlug, newDsSlug);

            await _objectSystem.MakeBucketAsync(newBucket, SafeGetRegion());

            var objects = _objectSystem.ListObjectsAsync(oldBucket, recursive: true).ToEnumerable().ToArray();

            foreach (var obj in objects)
            {
                if (obj.IsDir)
                {
                    _logger.LogDebug($"Skipping folder '{obj.Key}'");
                    continue;
                }

                _logger.LogDebug($"Moving '{obj.Key}'");
                // TODO: Metadata?
                await _objectSystem.CopyObjectAsync(oldBucket, obj.Key, newBucket);
            }

            _logger.LogInformation("Deleting old bucket");

            await _objectSystem.RemoveBucketAsync(oldBucket);

            _logger.LogInformation("Old bucket deleted");

        }

        public async Task<FileDescriptorDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size, bool recreate = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, dsSlug, new[] { path });

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var destFilePath = Path.Combine(Path.GetTempPath(), "out-" + Path.ChangeExtension(fileName, ".jpg"));
            var sourceFilePath = Path.Combine(Path.GetTempPath(), fileName);

            try
            {
                var obj = await InternalGet(orgSlug, dsSlug, path);
                await File.WriteAllBytesAsync(sourceFilePath, obj.Data);

                // Request a cache-aware ddb implementation
                var ddb = _ddbFactory
                    .GetDdb(orgSlug, dsSlug)
                    .UseCache(_distributedCache, _settings.CacheProvider?.Settings.ToObject<CacheProviderSettings>());
                
                ddb.GenerateThumbnail(sourceFilePath, size ?? DefaultThumbnailSize, destFilePath);

                var memory = new MemoryStream(await File.ReadAllBytesAsync(destFilePath));
                memory.Reset();

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "image/jpeg",
                    Name = Path.ChangeExtension(fileName, ".jpg")
                };
            }
            finally
            {
                if (File.Exists(destFilePath)) File.Delete(destFilePath);
                if (File.Exists(sourceFilePath)) File.Delete(sourceFilePath);
            }

        }

        #region Downloads
        public async Task<string> GetDownloadPackage(string orgSlug, string dsSlug, string[] paths, DateTime? expiration = null, bool isPublic = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, dsSlug, paths);

            var currentUser = await _authManager.GetCurrentUser();

            var downloadPackage = new DownloadPackage
            {
                CreationDate = DateTime.Now,
                Dataset = ds,
                ExpirationDate = expiration,
                Paths = paths,
                UserName = currentUser.UserName,
                IsPublic = isPublic
            };

            await _context.DownloadPackages.AddAsync(downloadPackage);
            await _context.SaveChangesAsync();

            return downloadPackage.Id.ToString();
        }

        private void EnsurePathsValidity(string orgSlug, string dsSlug, string[] paths)
        {

            if (paths == null || !paths.Any())
                // Everything
                return;

            if (paths.Any(path => path.Contains("*") || path.Contains("?") || string.IsNullOrWhiteSpace(path)))
                throw new ArgumentException("Wildcards or empty paths are not supported");

            if (paths.Length != paths.Distinct().Count())
                throw new ArgumentException("Duplicate paths");

            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);

            foreach (var path in paths)
            {
                var res = ddb.Search(path)?.ToArray();

                if (res == null || !res.Any())
                    throw new ArgumentException($"Invalid path: '{path}'");
            }
        }

        public async Task<FileDescriptorDto> Download(string orgSlug, string dsSlug, string packageId)
        {
            await _utils.GetDataset(orgSlug, dsSlug, checkOwnership: false);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (packageId == null)
                throw new ArgumentException("No package id provided");

            if (!Guid.TryParse(packageId, out var packageGuid))
                throw new ArgumentException("Invalid package id: expected guid");

            var package = _context.DownloadPackages.FirstOrDefault(item => item.Id == packageGuid);

            if (package == null)
                throw new ArgumentException($"Cannot find package with id '{packageId}'");

            var user = await _authManager.GetCurrentUser();

            // If we are not logged-in and this is not a public package
            if (user == null && !package.IsPublic)
                throw new UnauthorizedException("Download not allowed");

            // If it has and expiration date
            if (package.ExpirationDate != null)
            {
                // If expired
                if (DateTime.Now > package.ExpirationDate)
                {
                    _context.DownloadPackages.Remove(package);
                    await _context.SaveChangesAsync();

                    throw new ArgumentException("This package is expired");
                }
            }
            // It's a one-time download
            else
            {
                _context.DownloadPackages.Remove(package);
                await _context.SaveChangesAsync();
            }

            return await GetFileDescriptor(orgSlug, dsSlug, package.Paths);

        }


        public async Task<FileDescriptorDto> Download(string orgSlug, string dsSlug, string[] paths)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, dsSlug, paths);

            return await GetFileDescriptor(orgSlug, dsSlug, paths);
        }

        private async Task<FileDescriptorDto> GetFileDescriptor(string orgSlug, string dsSlug, string[] paths)
        {
            var ddb = _ddbFactory.GetDdb(orgSlug, dsSlug);

            var filePaths = new List<string>();

            if (paths != null)
            {

                foreach (var path in paths)
                {
                    var entries = ddb.Search(path)?.ToArray();

                    if (entries == null || !entries.Any())
                        throw new ArgumentException($"Cannot find any file path matching '{path}'");

                    filePaths.AddRange(entries.Select(item => item.Path));
                }

            }
            else
            {
                // Select everything
                filePaths = ddb.Search(null)
                    .Select(entry => entry.Path)
                    .ToList();
            }

            _logger.LogInformation($"Found {filePaths.Count} paths");

            FileDescriptorDto descriptor;

            // If there is just one file we return it
            if (filePaths.Count == 1)
            {
                var filePath = filePaths.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                descriptor = new FileDescriptorDto
                {
                    ContentStream = new MemoryStream(),
                    Name = Path.GetFileName(filePath),
                    ContentType = MimeUtility.GetMimeMapping(filePath)
                };

                await WriteObjectContentStream(orgSlug, dsSlug, filePath, descriptor.ContentStream);

                descriptor.ContentStream.Reset();
            }
            // Otherwise we zip everything together and return the package
            else
            {
                descriptor = new FileDescriptorDto
                {
                    Name = $"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                    ContentStream = new MemoryStream(),
                    ContentType = "application/zip"
                };

                // Hopefully all the folders are correctly ordered
                filePaths.Sort();

                using (var archive = new ZipArchive(descriptor.ContentStream, ZipArchiveMode.Create, true))
                {
                    foreach (var path in filePaths)
                    {
                        _logger.LogInformation($"Zipping: '{path}'");

                        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await WriteObjectContentStream(orgSlug, dsSlug, path, entryStream);
                    }
                }

                descriptor.ContentStream.Reset();
            }

            return descriptor;
        }

        private async Task WriteObjectContentStream(string orgSlug, string dsSlug, string path, Stream stream)
        {
            var bucketName = string.Format(BucketNameFormat, orgSlug, dsSlug);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogInformation("Bucket does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetRegion());

                _logger.LogInformation("Bucket created");
            }

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}' in storage provider");

            _logger.LogInformation($"Getting object '{path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, path, s => s.CopyTo(stream));
        }

        #endregion
    }
}
