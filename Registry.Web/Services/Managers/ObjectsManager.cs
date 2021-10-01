using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DDB.Bindings;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly ICacheManager _cacheManager;
        private readonly IBackgroundJobsProcessor _backgroundJob;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        // TODO: Could be moved to config
        private const int DefaultThumbnailSize = 512;

        private readonly string _location;

        private bool IsReservedPath(string path)
        {
            return path.StartsWith(_ddbManager.DatabaseFolderName);
        }

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IObjectSystem objectSystem,
            IOptions<AppSettings> settings,
            IDdbManager ddbManager,
            IUtils utils,
            IAuthManager authManager,
            ICacheManager cacheManager,
            IBackgroundJobsProcessor backgroundJob)
        {
            _logger = logger;
            _context = context;
            _objectSystem = objectSystem;
            _ddbManager = ddbManager;
            _utils = utils;
            _authManager = authManager;
            _cacheManager = cacheManager;
            _backgroundJob = backgroundJob;
            _settings = settings.Value;

            _location = _settings.SafeGetLocation(_logger);

        }

        public async Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path = null, bool recursive = false)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In List('{orgSlug}/{dsSlug}')");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Searching in '{path}'");

            var files = ddb.Search(path, recursive).Select(file => file.ToDto()).ToArray();

            _logger.LogInformation($"Found {files.Length} objects");

            return files;
        }

        public async Task<IEnumerable<ObjectDto>> Search(string orgSlug, string dsSlug, string query = null, string path = null, bool recursive = true)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Search('{orgSlug}/{dsSlug}')");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Searching in '{path}' -> {query} ({(recursive ? 'r' : 'n')}");

            var files = (from entry in ddb.Search(path, recursive)
                        let name = Path.GetFileName(entry.Path)
                        where FileSystemName.MatchesSimpleExpression(query, name)
                        select entry.ToDto()).ToArray();

            _logger.LogInformation($"Found {files.Length} objects");

            return files;
        }

        public async Task<ObjectRes> Get(string orgSlug, string dsSlug, string path)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Get('{orgSlug}/{dsSlug}')");

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path should not be null");

            return await InternalGet(orgSlug, ds.InternalRef, path);
        }

        private async Task SafeGetFile(string orgSlug, Guid internalRef, string path, string destFile, TimeSpan? maxWaitTime = null)
        {
            var bucketName = _utils.GetBucketName(orgSlug, internalRef);

            maxWaitTime ??= new TimeSpan(0, 0, 1, 0);

            if (!File.Exists(destFile))
            {
                // ReSharper disable once MethodHasAsyncOverload
                File.WriteAllText(destFile + ".lock", Environment.TickCount.ToString());

                await using var file = File.OpenWrite(destFile);
                await _objectSystem.GetObjectAsync(bucketName, path, stream => stream.CopyTo(file));

                File.Delete(destFile + ".lock");
            }
            else
            {
                var time = DateTime.Now;
                while (File.Exists(destFile + ".lock"))
                {
                    Thread.Sleep(50);

                    if (DateTime.Now > time + maxWaitTime.Value)
                        throw new InvalidOperationException("Wait time expired, cannot get file");
                }

            }

        }

        private async Task<ObjectRes> InternalGet(string orgSlug, Guid internalRef, string path)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            if (!ddb.EntryExists(path))
                throw new NotFoundException($"Cannot find '{path}'");

            var bucketName = _utils.GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            await _objectSystem.EnsureBucketExists(bucketName, _location, _logger);

            var res = ddb.GetEntry(path);

            if (res.Type == EntryType.Directory)
                throw new InvalidOperationException("Cannot get a folder, we are supposed to deal with a file!");

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

        public async Task<ObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data)
        {
            await using var stream = new MemoryStream(data);
            stream.Reset();
            return await AddNew(orgSlug, dsSlug, path, stream);
        }

        public async Task<ObjectDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In AddNew('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            await _objectSystem.EnsureBucketExists(bucketName, _location, _logger);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            // If it's a folder
            if (stream == null)
            {
                if (ddb.EntryExists(path))
                    throw new InvalidOperationException("Cannot create a folder on another entry");

                if (path == ddb.DatabaseFolderName)
                    throw new InvalidOperationException($"'{ddb.DatabaseFolderName}' is a reserved folder name");

                _logger.LogInformation("Adding folder to DDB");

                // Add to DDB
                ddb.Add(path);

                _logger.LogInformation("Added to DDB");

                return new ObjectDto
                {
                    Path = path,
                    Type = EntryType.Directory,
                    Size = 0
                };
            }

            // Check user storage space
            await _utils.CheckCurrentUserStorage(stream.Length);

            // TODO: I highly doubt the robustness of this 
            var contentType = MimeTypes.GetMimeType(path);

            var tempFileName = Path.GetTempFileName();

            // Write down the file
            await using (var tempFileStream = File.OpenWrite(tempFileName))
                await stream.CopyToAsync(tempFileStream);
            
            _logger.LogInformation("File uploaded, adding to DDB");

            // Add to DDB
            await using (var tempFileStream = File.OpenRead(tempFileName))
                ddb.Add(path, tempFileStream);

            _logger.LogInformation("Added to DDB");

            var entry = ddb.GetEntry(path);

            if (entry == null)
                throw new InvalidOperationException("Cannot find just added file!");

            var obj = entry.ToDto();

            // NOTE: We perform the actual upload asynchronously, we will move it to hangfire soon or later
#pragma warning disable 4014
            var uploadTask = Task.Run(async () =>
#pragma warning restore 4014
            {

                _logger.LogInformation($"Uploading '{path}' (size {stream.Length}) to bucket '{bucketName}'");

                await using var tmpStream = File.OpenRead(tempFileName);
                await _objectSystem.PutObjectAsync(bucketName, path, tmpStream, tmpStream.Length, contentType);

            });

            if (ddb.IsBuildable(obj.Path))
            {
                _logger.LogInformation("This is a point cloud, we need to build it!");

                var tempBuildFolder = Path.Combine(Path.GetTempPath(), nameof(HangfireUtils), CommonUtils.RandomString(16));
                _logger.LogInformation($"Destination temp folder '{tempBuildFolder}'");

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildWrapper(ddb, path, tempFileName, tempBuildFolder, true, null));

                _logger.LogInformation("Background job id is " + jobId);

#pragma warning disable 4014
                uploadTask.ContinueWith(_ =>
#pragma warning restore 4014
                {
                    _logger.LogInformation(
                        $"Scheduling deletion of temp file '{tempFileName}' after job {jobId} completes");

                    var deleteId = _backgroundJob.ContinueJobWith(jobId, () => HangfireUtils.SafeDelete(tempFileName, null));

                    var destBucketFolder = CommonUtils.SafeCombine(ddb.DatabaseFolderName, ddb.BuildFolderName);
                    
                    // NOTE: If the user deletes the LAZ file while it is being built this process goes on and saves it on storage anyways
                    //       We could check if the file is still in ddb, otherwise cancel the folder sync. Too early for this?

                    // Put it on storage
                    var syncId = _backgroundJob.ContinueJobWith(deleteId, () =>
                        HangfireUtils.SyncFolder(_objectSystem, tempBuildFolder, bucketName, destBucketFolder, null));

                    _backgroundJob.ContinueJobWith(syncId, () => HangfireUtils.SafeDelete(tempBuildFolder, null));

                });
            }


            return obj;
        }

        public async Task Move(string orgSlug, string dsSlug, string source, string dest)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Move('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            await _objectSystem.EnsureBucketExists(bucketName, _location, _logger);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var sourceEntry = ddb.GetEntry(source);

            // Checking if source exists
            if (sourceEntry == null)
                throw new InvalidOperationException("Cannot find source entry: '" + source + "'");

            if (IsReservedPath(dest))
                throw new InvalidOperationException($"'{dest}' is a reserved path");

            // Short circuit!
            if ((dest + "/").StartsWith(source + "/"))
                throw new InvalidOperationException("Cannot move a path onto itself or one of its descendants");
            
            var destEntry = ddb.GetEntry(dest);

            if (destEntry != null)
            {
                if (sourceEntry.Type == EntryType.Directory && destEntry.Type != EntryType.Directory)
                    throw new ArgumentException("Cannot move a folder on a file");

                if (sourceEntry.Type != EntryType.Directory && destEntry.Type == EntryType.Directory)
                    throw new ArgumentException("Cannot move a file on a folder");
            }

            var src = ddb.Search(source).Where(item => item.Type != EntryType.Directory).ToArray();

            switch (src.Length)
            {
                // If it's an empty folder
                case 0:

                    _logger.LogInformation("Moving empty folder, nothing to do in object system");

                    break;

                case 1:

                    _logger.LogInformation($"Copying object '{source}' to '{dest}'");
                    await _objectSystem.CopyObjectAsync(bucketName, source, bucketName, dest);

                    _logger.LogInformation("Removing source object");
                    await _objectSystem.RemoveObjectAsync(bucketName, source);

                    break;

                // If it's a folder
                default:

                    _logger.LogInformation($"Moving folder '{source}' to '{dest}'");

                    await _objectSystem.MoveDirectory(bucketName, source, dest);
                    _logger.LogInformation("Move OK");

                    break;
            }

            _logger.LogInformation("Performing ddb move");
            ddb.Move(source, dest);

            if (!ddb.EntryExists(dest))
                throw new InvalidOperationException($"Cannot find destination '{dest}' after move, something wrong with ddb");

            _logger.LogInformation("Move OK");


        }

        public async Task Delete(string orgSlug, string dsSlug, string path)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Delete('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            if (IsReservedPath(path))
                throw new InvalidOperationException($"'{path}' is a reserved path");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (!ddb.EntryExists(path))
                throw new BadRequestException($"Path '{path}' not found in dataset");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);
            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            _logger.LogInformation("Removing from DDB");

            var objs = ddb.Search(path, true).ToArray();

            ddb.Remove(path);

            foreach (var obj in objs.Where(item => item.Type != EntryType.Directory))
            {
                _logger.LogInformation($"Deleting '{obj.Path}'");
                await _objectSystem.RemoveObjectAsync(bucketName, obj.Path);

                await RemoveBuildFiles(bucketName, obj.Hash);

            }

            _logger.LogInformation("Deletion complete");


        }

        private async Task RemoveBuildFiles(string bucketName, string hash, CancellationToken cancellationToken = default)
        {
            // TODO: This path calculation should not be done here (IMHO)
            var buildPath = CommonUtils.SafeCombine(_ddbManager.DatabaseFolderName, _ddbManager.BuildFolderName, hash);

            var buildFiles = _objectSystem.ListObjectsAsync(bucketName, buildPath, true)
                .ToEnumerable()
                .Where(item => !item.IsDir)
                .Select(item => item.Key)
                .ToArray();

            await _objectSystem.RemoveObjectsAsync(bucketName, buildFiles, cancellationToken);

        }

        public async Task DeleteAll(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DeleteAll('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogWarning($"Asked to remove non-existing bucket '{bucketName}'");
            }
            else
            {
                _logger.LogInformation("Deleting bucket");
                await _objectSystem.RemoveBucketAsync(bucketName);
                _logger.LogInformation("Bucket deleted");
            }

            _logger.LogInformation("Removing DDB");

            _ddbManager.Delete(orgSlug, ds.InternalRef);

        }

        public async Task<FileDescriptorDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size, bool recreate = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In GenerateThumbnail('{orgSlug}/{dsSlug}')");

            // Fix fox '/img.png' -> 'img.png'
            if (path.StartsWith('/')) path = path.Substring(1);

            var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out IDdb ddb);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var sourceFilePath = Path.GetTempFileName();

            try
            {
                byte[] thumb = await _cacheManager.GenerateThumbnail(ddb, sourceFilePath, entry.Hash, size ?? DefaultThumbnailSize, async () =>
                {
                    var obj = await InternalGet(orgSlug, ds.InternalRef, path);
                    await File.WriteAllBytesAsync(sourceFilePath, obj.Data);
                });

                var memory = new MemoryStream(thumb);

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "image/jpeg",
                    Name = Path.ChangeExtension(fileName, ".jpg")
                };
            }
            finally
            {
                if (File.Exists(sourceFilePath) && !CommonUtils.SafeDelete(sourceFilePath))
                    _logger.LogWarning($"Cannot delete source file '{sourceFilePath}'");
            }

        }

        public async Task<FileDescriptorDto> GenerateTile(string orgSlug, string dsSlug, string path, int tz, int tx, int ty, bool retina)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In GenerateTile('{orgSlug}/{dsSlug}')");

            EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var sourceFilePath = Path.Combine(Path.GetTempPath(), nameof(GenerateTile), orgSlug, dsSlug, path);

            var dirName = Path.GetDirectoryName(sourceFilePath);
            if (dirName != null)
                Directory.CreateDirectory(dirName);

            await SafeGetFile(orgSlug, ds.InternalRef, path, sourceFilePath);

            try
            {

                _logger.LogInformation($"Generating tile from '{sourceFilePath}'");
                var destFilePath = ddb.GenerateTile(sourceFilePath, tz, tx, ty, retina, true);

                var memory = new MemoryStream(await File.ReadAllBytesAsync(destFilePath));
                memory.Reset();

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "image/png",
                    Name = fileName
                };
            }
            catch (InvalidOperationException ex)
            {
                // NOTE: This is the definition of self-inflicted wound
                if (ex.InnerException != null && ex.InnerException.Message.Contains("Out of bounds", StringComparison.OrdinalIgnoreCase))
                    throw new NotFoundException("Tile out of bounds");

                throw;
            }
        }

        #region Downloads
        public async Task<string> GetDownloadPackage(string orgSlug, string dsSlug, string[] paths, DateTime? expiration = null, bool isPublic = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In GetDownloadPackage('{orgSlug}/{dsSlug}')");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

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

        public async Task<FileDescriptorDto> DownloadPackage(string orgSlug, string dsSlug, string packageId)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug, checkOwnership: false);

            _logger.LogInformation($"In DownloadPackage('{orgSlug}/{dsSlug}')");

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

            return await GetOfflineFileDescriptor(orgSlug, dsSlug, ds.InternalRef, package.Paths);

        }


        public async Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DownloadStream('{orgSlug}/{dsSlug}')");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return GetFileStreamDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

        public async Task<FileDescriptorDto> Download(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Download('{orgSlug}/{dsSlug}')");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return await GetOfflineFileDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

        private FileStreamDescriptor GetFileStreamDescriptor(string orgSlug, string dsSlug, Guid internalRef, string[] paths)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var (files, folders, includeDdb) = GetFilePaths(paths, ddb);

            FileStreamDescriptor streamDescriptor;

            // If there is just one file we return it
            if (files.Length == 1 && paths?.Length == 1 && files[0] == paths[0])
            {
                var filePath = files.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                streamDescriptor = new FileStreamDescriptor(Path.GetFileName(filePath), MimeUtility.GetMimeMapping(filePath),
                    orgSlug, internalRef, files, null, FileDescriptorType.Single, _objectSystem, this, _logger, _ddbManager, _utils);

            }
            // Otherwise we zip everything together and return the package
            else
            {
                streamDescriptor = new FileStreamDescriptor($"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                    "application/zip", orgSlug, internalRef, files, folders,
                    includeDdb ? FileDescriptorType.Dataset : FileDescriptorType.Multiple, _objectSystem, this,
                    _logger, _ddbManager, _utils);

            }

            return streamDescriptor;
        }

        private async Task<FileDescriptorDto> GetOfflineFileDescriptor(string orgSlug, string dsSlug, Guid internalRef, string[] paths)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var (files, folders, includeDdb) = GetFilePaths(paths, ddb);

            FileDescriptorDto descriptor;

            // If there is just one file we return it
            if (files.Length == 1 && paths?.Length == 1 && files[0] == paths[0])
            {
                var filePath = files.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                descriptor = new FileDescriptorDto
                {
                    ContentStream = new MemoryStream(),
                    Name = Path.GetFileName(filePath),
                    ContentType = MimeUtility.GetMimeMapping(filePath)
                };

                await WriteObjectContentStream(orgSlug, internalRef, filePath, descriptor.ContentStream);

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

                using (var archive = new ZipArchive(descriptor.ContentStream, ZipArchiveMode.Create, true))
                {
                    foreach (var path in files)
                    {
                        _logger.LogInformation($"Zipping: '{path}'");

                        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                        await using var entryStream = entry.Open();

                        await WriteObjectContentStream(orgSlug, internalRef, path, entryStream);
                    }

                    // We treat folders separately because if they are empty they would not be included in the archive
                    if (folders != null)
                    {
                        foreach (var folder in folders)
                            archive.CreateEntry(folder + "/");
                    }

                    // Include ddb folder
                    if (includeDdb)
                    {
                        archive.CreateEntryFromAny(Path.Combine(ddb.DatasetFolderPath, ddb.DatabaseFolderName), string.Empty, new[] { ddb.BuildFolderPath });
                    }
                }

                descriptor.ContentStream.Reset();
            }

            return descriptor;
        }

        private async Task WriteObjectContentStream(string orgSlug, Guid internalRef, string path, Stream stream)
        {
            var bucketName = _utils.GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new NotFoundException($"Cannot find bucket '{bucketName}'");

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}' in storage provider");

            _logger.LogInformation($"Getting object '{path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, path, s => s.CopyTo(stream));

        }

        private (string[] files, string[] folders, bool includeDdb) GetFilePaths(string[] paths, IDdb ddb)
        {
            string[] files;
            string[] folders;

            var includeDdb = false;

            if (paths != null)
            {
                var tempFiles = new List<string>();
                var tempFolders = new List<string>();

                foreach (var path in paths)
                {
                    var entry = ddb.GetEntry(path);

                    if (entry.Type == EntryType.Directory)
                    {
                        // We are in recursive mode because the paths could contain other folders that we need to expand
                        var items = ddb.Search(path, true)?.ToArray();

                        if (items == null)
                            throw new InvalidOperationException("Ddb is empty, what should I get?");

                        tempFolders.Add(path);
                        tempFiles.AddRange(items.Where(item => item.Type != EntryType.Directory).Select(item => item.Path));
                        tempFolders.AddRange(items.Where(item => item.Type == EntryType.Directory).Select(item => item.Path));
                    }
                    else
                    {
                        tempFiles.Add(path);
                    }
                }

                // Get rid of possible duplicates and sort
                files = tempFiles.Distinct().OrderBy(item => item).ToArray();
                folders = tempFolders.Distinct().OrderBy(item => item).ToArray();
            }
            else
            {
                var entries = ddb.Search(null, true)?.ToArray();

                if (entries == null || !entries.Any())
                    throw new InvalidOperationException("Ddb is empty, what should I get?");

                // Select everything and sort
                files = entries
                    .Where(entry => entry.Type != EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
                    .ToArray();

                folders = entries
                    .Where(entry => entry.Type == EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
                    .ToArray();

                // We include the ddb folder only when asked for the entire dataset
                includeDdb = true;
            }

            _logger.LogInformation($"Found {files.Length} paths");
            return (files, folders, includeDdb);
        }

        #endregion

        #region Utils

        private DdbEntry EnsurePathValidity(string orgSlug, Guid internalRef, string path)
        {
            return EnsurePathValidity(orgSlug, internalRef, path, out IDdb ddb);
        }

        private DdbEntry EnsurePathValidity(string orgSlug, Guid internalRef, string path, out IDdb ddb)
        {

            EnsureNoWildcardOrEmptyPaths(path);

            ddb = _ddbManager.Get(orgSlug, internalRef);

            var res = ddb.Search(path)?.ToArray();

            if (res == null || !res.Any())
                throw new ArgumentException($"Invalid path: '{path}'");

            return res.First();
        }

        private void EnsureNoWildcardOrEmptyPaths(string path)
        {
            if (path.Contains("*") || path.Contains("?") || string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Wildcards or empty paths are not supported");
        }

        private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths)
        {
            EnsurePathsValidity(orgSlug, internalRef, paths, out IDdb ddb);
        }

        private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths, out IDdb ddb)
        {
            ddb = null;

            if (paths == null || !paths.Any())
                // Everything
                return;

            if (paths.Any(path => path.Contains("*") || path.Contains("?") || string.IsNullOrWhiteSpace(path)))
                throw new ArgumentException("Wildcards or empty paths are not supported");

            if (paths.Length != paths.Distinct().Count())
                throw new ArgumentException("Duplicate paths");

            ddb = _ddbManager.Get(orgSlug, internalRef);

            foreach (var path in paths)
                if (!ddb.EntryExists(path))
                    throw new ArgumentException($"Invalid path: '{path}'");

        }

        #endregion


        public async Task<FileDescriptorDto> GetDdb(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In GetDdb('{orgSlug}/{dsSlug}')");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                // We could do this fully in memory BUT it's not a strict requirement by now: ddb folders are not huge (yet)
                ZipFile.CreateFromDirectory(ddb.DatasetFolderPath, tempFile, CompressionLevel.NoCompression, false);

                await using var s = File.OpenRead(tempFile);
                var memory = new MemoryStream();
                await s.CopyToAsync(memory);
                memory.Reset();

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "application/zip",
                    Name = $"{orgSlug}-{dsSlug}-ddb.zip"
                };

            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        public async Task Build(string orgSlug, string dsSlug, string path, bool force = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Build('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to build dataset");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            await _objectSystem.EnsureBucketExists(bucketName, _location, _logger);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var entry = ddb.GetEntry(path);

            // Checking if path exists
            if (entry == null)
                throw new InvalidOperationException($"Cannot find source entry: '{path}'");

            // Nothing to do here
            if (!ddb.IsBuildable(entry.Path))
            {
                _logger.LogInformation($"'{entry.Path}' is not buildable, nothing to do here");
                return;
            }

            var obj = await InternalGet(orgSlug, ds.InternalRef, path);

            var tempFileName = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tempFileName, obj.Data);

                HangfireUtils.BuildWrapper(ddb, path, tempFileName, null, false, null);

                // Put it on storage
                HangfireUtils.SyncBuildFolder(_objectSystem, ddb, entry, bucketName, null);

                // Delete local build folder
                CommonUtils.SafeDeleteFolder(Path.Combine(BuildBasePath, entry.Hash));

            }
            finally
            {
                CommonUtils.SafeDelete(tempFileName);
            }

        }

        #region Build

        public async Task<FileDescriptorDto> GetBuildFile(string orgSlug, string dsSlug, string hash, string path)
        {
            _logger.LogInformation($"In GetBuildFile('{orgSlug}/{dsSlug}', '{hash}', '{path}')");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            EnsureNoWildcardOrEmptyPaths(path);

            Debug.Assert(path != null, nameof(path) + " != null");
            if (Path.IsPathRooted(path) || path.Contains(".."))
                throw new ArgumentException("Rooted or relative paths are not supported");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);
            _logger.LogInformation($"Using bucket '{bucketName}'");

            var destPath = CommonUtils.SafeCombine(BuildBasePath, hash, path);
            _logger.LogInformation($"Using actual path '{destPath}'");

            _logger.LogInformation($"Getting object '{destPath}' in bucket '{bucketName}'");

            var fdt = new TaskCompletionSource<FileDescriptorDto>();

            // Executed asynchronously, we do not wait for this to complete
            // so that we can stream the result.
            _ = _objectSystem.GetObjectAsync(bucketName, destPath, stream =>
            {
                fdt.SetResult(new FileDescriptorDto
                {
                    ContentStream = stream,
                    ContentType = MimeUtility.GetMimeMapping(path),
                    Name = Path.GetFileName(path)
                });
            }).ContinueWith(t =>
            {
                _logger.LogError(t.Exception, $"Exception in GetBuildFile('{orgSlug}', '{dsSlug}', '{hash}', '{path}')");
                fdt.SetException(t.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted);

            return await fdt.Task;
        }

        // Base build folder path (example: .ddb/build)
        private string BuildBasePath => CommonUtils.SafeCombine(_ddbManager.DatabaseFolderName, _ddbManager.BuildFolderName);

        public async Task<bool> CheckBuildFile(string orgSlug, string dsSlug, string hash, string path)
        {

            _logger.LogInformation($"In CheckBuildFile('{orgSlug}/{dsSlug}', '{hash}', '{path}')");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            EnsureNoWildcardOrEmptyPaths(path);

            Debug.Assert(path != null, nameof(path) + " != null");
            if (Path.IsPathRooted(path) || path.Contains(".."))
                throw new ArgumentException("Rooted or relative paths are not supported");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);
            _logger.LogInformation($"Using bucket '{bucketName}'");

            var destPath = CommonUtils.SafeCombine(BuildBasePath, hash, path);
            _logger.LogInformation($"Using actual path '{destPath}'");

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, destPath);

            return objInfo != null;
        }

        #endregion
    }
}
