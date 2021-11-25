using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Linq;
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
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly ICacheManager _cacheManager;
        //private readonly IS3BridgeManager _bridgeManager;
        private readonly IFileSystem _fs;
        private readonly IBackgroundJobsProcessor _backgroundJob;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        // TODO: Could be moved to config
        private const int DefaultThumbnailSize = 512;

        private bool IsReservedPath(string path)
        {
            return path.StartsWith(_ddbManager.DatabaseFolderName);
        }

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IOptions<AppSettings> settings,
            IDdbManager ddbManager,
            IUtils utils,
            IAuthManager authManager,
            ICacheManager cacheManager,
            //IS3BridgeManager bridgeManager,
            IFileSystem fs,
            IBackgroundJobsProcessor backgroundJob)
        {
            _logger = logger;
            _context = context;
            _ddbManager = ddbManager;
            _utils = utils;
            _authManager = authManager;
            _cacheManager = cacheManager;
            //_bridgeManager = bridgeManager;
            _fs = fs;
            _backgroundJob = backgroundJob;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path = null,
            bool recursive = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In List('{orgSlug}/{dsSlug}')");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Searching in '{path}'");

            var files = (await ddb.SearchAsync(path, recursive)).Select(file => file.ToDto()).ToArray();

            _logger.LogInformation($"Found {files.Length} objects");

            return files;
        }

        public async Task<IEnumerable<ObjectDto>> Search(string orgSlug, string dsSlug, string query = null,
            string path = null, bool recursive = true)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Search('{orgSlug}/{dsSlug}')");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Searching in '{path}' -> {query} ({(recursive ? 'r' : 'n')}");

            var files = (from entry in await ddb.SearchAsync(path, recursive)
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

        private async Task<ObjectRes> InternalGet(string orgSlug, Guid internalRef, string path)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var entry = await ddb.GetEntryAsync(path);

            if (entry == null)
                throw new NotFoundException($"Cannot find '{path}'");

            if (entry.Type == EntryType.Directory)
                throw new InvalidOperationException("Cannot get a folder, we are supposed to deal with a file!");

            return new ObjectRes
            {
                Hash = entry.Hash,
                Name = Path.GetFileName(entry.Path),
                Size = entry.Size,
                Type = entry.Type,
                ContentType = MimeTypes.GetMimeType(entry.Path),
                PhysicalPath = Path.GetFullPath(ddb.GetLocalPath(entry.Path))
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

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            // If it's a folder
            if (stream == null)
            {
                if (await ddb.EntryExistsAsync(path))
                    throw new InvalidOperationException("Cannot create a folder on another entry");

                if (path == ddb.DatabaseFolderName)
                    throw new InvalidOperationException($"'{ddb.DatabaseFolderName}' is a reserved folder name");

                _logger.LogInformation("Adding folder to DDB");

                // Add to DDB
                await ddb.AddAsync(path);

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

            var localFilePath = ddb.GetLocalPath(path);
            CommonUtils.EnsureSafePath(localFilePath);

            _logger.LogInformation($"Local file path is '{localFilePath}'");

            // Write down the file
            await using (var localFileStream = File.OpenWrite(localFilePath))
                await stream.CopyToAsync(localFileStream);

            _logger.LogInformation("File saved, adding to DDB");
            ddb.AddRaw(localFilePath);

            _logger.LogInformation("Added to DDB, checking entry now...");

            var entry = await ddb.GetEntryAsync(path);

            if (entry == null)
                throw new InvalidOperationException("Cannot find just added file!");

            _logger.LogInformation("Entry OK");

            var obj = entry.ToDto();

            if (await ddb.IsBuildableAsync(obj.Path))
            {
                _logger.LogInformation("This is a point cloud, we need to build it!");

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildWrapper(ddb, path, true, null));

                _logger.LogInformation("Background job id is " + jobId);
            }

            return obj;
        }

        public async Task Move(string orgSlug, string dsSlug, string source, string dest)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Move('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var sourceEntry = await ddb.GetEntryAsync(source);

            // Checking if source exists
            if (sourceEntry == null)
                throw new InvalidOperationException("Cannot find source entry: '" + source + "'");

            if (IsReservedPath(dest))
                throw new InvalidOperationException($"'{dest}' is a reserved path");

            // Short circuit!
            if ((dest + "/").StartsWith(source + "/"))
                throw new InvalidOperationException("Cannot move a path onto itself or one of its descendants");

            var destEntry = await ddb.GetEntryAsync(dest);

            if (destEntry != null)
            {
                if (sourceEntry.Type == EntryType.Directory && destEntry.Type != EntryType.Directory)
                    throw new ArgumentException("Cannot move a folder on a file");

                if (sourceEntry.Type != EntryType.Directory && destEntry.Type == EntryType.Directory)
                    throw new ArgumentException("Cannot move a file on a folder");
            }

            var src = (await ddb.SearchAsync(source)).Where(item => item.Type != EntryType.Directory).ToArray();

            switch (src.Length)
            {
                // If it's an empty folder
                case 0:

                    _logger.LogInformation("Moving empty folder, nothing to do in object system");

                    break;

                case 1:

                    var sourceLocalFilePath = ddb.GetLocalPath(source);
                    var destLocalFilePath = ddb.GetLocalPath(dest);
                    
                    _logger.LogInformation($"Moving object '{source}' to '{dest}'");
                    
                    _fs.Move(sourceLocalFilePath, destLocalFilePath);

                    break;

                // If it's a folder
                default:

                    _logger.LogInformation($"Moving folder '{source}' to '{dest}'");

                    var sourceLocalFolderPath = ddb.GetLocalPath(source);
                    var destLocalFolderPath = ddb.GetLocalPath(dest);
                    
                    _fs.FolderMove(sourceLocalFolderPath, destLocalFolderPath);
                    //await _objectSystem.MoveDirectory(bucketName, source, dest);
                    _logger.LogInformation("Move OK");

                    break;
            }

            _logger.LogInformation("Performing ddb move");
            await ddb.MoveAsync(source, dest);

            if (!await ddb.EntryExistsAsync(dest))
                throw new InvalidOperationException(
                    $"Cannot find destination '{dest}' after move, something wrong with ddb");

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

            if (!await ddb.EntryExistsAsync(path))
                throw new BadRequestException($"Path '{path}' not found in dataset");

            _logger.LogInformation("Removing from DDB");

            var objs = (await ddb.SearchAsync(path, true)).ToArray();

            await ddb.RemoveAsync(path);

            foreach (var obj in objs.Where(item => item.Type != EntryType.Directory))
            {
                _logger.LogInformation($"Deleting '{obj.Path}'");

                var objLocalPath = ddb.GetLocalPath(obj.Path);

                if (!_fs.Exists(objLocalPath))
                    throw new InvalidOperationException(
                        $"Cannot find local file '{objLocalPath}' for object '{obj.Path}'");

                _fs.Delete(objLocalPath);

                await _cacheManager.Clear(MagicStrings.ThumbnailCacheSeed,obj.Hash);
                await _cacheManager.Clear(MagicStrings.TileCacheSeed,obj.Hash);
                
            }

            _logger.LogInformation("Deletion complete");
        }

        public async Task DeleteAll(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DeleteAll('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");
            
            _ddbManager.Delete(orgSlug, ds.InternalRef);
        }

        public async Task<ObjectRes> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size,
            bool recreate = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In GenerateThumbnail('{orgSlug}/{dsSlug}')");

            // Fix fox '/img.png' -> 'img.png'
            if (path.StartsWith('/')) path = path[1..];

            var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var sourcePath = GetBuildSource(entry);
            var localPath = ddb.GetLocalPath(sourcePath);

            var thumbPath = await _cacheManager.Get(MagicStrings.ThumbnailCacheSeed, entry.Hash, ddb, localPath, size ?? DefaultThumbnailSize);

            return new ObjectRes
            {
                Name = Path.ChangeExtension(fileName, ".jpg"),
                PhysicalPath = Path.GetFullPath(thumbPath),
                ContentType = "image/jpeg"
            };

        }

        public async Task<ObjectRes> GenerateTile(string orgSlug, string dsSlug, string path, int tz, int tx,
            int ty, bool retina)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In GenerateTile('{orgSlug}/{dsSlug}')");

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

            var sourcePath = GetBuildSource(entry);
            var localPath = ddb.GetLocalPath(sourcePath);

            try
            {

                var tilePath = await _cacheManager.Get("tile", entry.Hash, ddb, localPath, entry.Hash, tx, ty, tz, retina);
                
                return new ObjectRes
                {
                    PhysicalPath = Path.GetFullPath(tilePath),
                    ContentType = "image/png",
                    Name = $"{ty}.png"
                };
                
            }
            catch (InvalidOperationException ex)
            {
                // NOTE: This is the definition of self-inflicted wound
                if (ex.InnerException != null &&
                    ex.InnerException.Message.Contains("Out of bounds", StringComparison.OrdinalIgnoreCase))
                    throw new NotFoundException("Tile out of bounds");

                throw;
            }
        }

        #region Downloads

        public async Task<string> GetDownloadPackage(string orgSlug, string dsSlug, string[] paths,
            DateTime? expiration = null, bool isPublic = false)
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

        // public async Task<FileDescriptorStreamDto> DownloadPackage(string orgSlug, string dsSlug, string packageId)
        // {
        //     var ds = await _utils.GetDataset(orgSlug, dsSlug, checkOwnership: false);
        //
        //     _logger.LogInformation($"In DownloadPackage('{orgSlug}/{dsSlug}')");
        //
        //     if (packageId == null)
        //         throw new ArgumentException("No package id provided");
        //
        //     if (!Guid.TryParse(packageId, out var packageGuid))
        //         throw new ArgumentException("Invalid package id: expected guid");
        //
        //     var package = _context.DownloadPackages.FirstOrDefault(item => item.Id == packageGuid);
        //
        //     if (package == null)
        //         throw new ArgumentException($"Cannot find package with id '{packageId}'");
        //
        //     var user = await _authManager.GetCurrentUser();
        //
        //     // If we are not logged-in and this is not a public package
        //     if (user == null && !package.IsPublic)
        //         throw new UnauthorizedException("Download not allowed");
        //
        //     // If it has and expiration date
        //     if (package.ExpirationDate != null)
        //     {
        //         // If expired
        //         if (DateTime.Now > package.ExpirationDate)
        //         {
        //             _context.DownloadPackages.Remove(package);
        //             await _context.SaveChangesAsync();
        //
        //             throw new ArgumentException("This package is expired");
        //         }
        //     }
        //     // It's a one-time download
        //     else
        //     {
        //         _context.DownloadPackages.Remove(package);
        //         await _context.SaveChangesAsync();
        //     }
        //
        //     return await GetOfflineFileDescriptor(orgSlug, dsSlug, ds.InternalRef, package.Paths);
        // }


        public async Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DownloadStream('{orgSlug}/{dsSlug}')");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return GetFileStreamDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

        // public async Task<FileDescriptorStreamDto> Download(string orgSlug, string dsSlug, string[] paths)
        // {
        //     var ds = await _utils.GetDataset(orgSlug, dsSlug);
        //
        //     _logger.LogInformation($"In Download('{orgSlug}/{dsSlug}')");
        //
        //     EnsurePathsValidity(orgSlug, ds.InternalRef, paths);
        //
        //     return await GetOfflineFileDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        // }

        private FileStreamDescriptor GetFileStreamDescriptor(string orgSlug, string dsSlug, Guid internalRef,
            string[] paths)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var (files, folders, includeDdb) = GetFilePaths(paths, ddb);

            FileStreamDescriptor streamDescriptor;

            // If there is just one file we return it
            if (files.Length == 1 && paths?.Length == 1 && files[0] == paths[0])
            {
                var filePath = files.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                streamDescriptor = new FileStreamDescriptor(Path.GetFileName(filePath),
                    MimeUtility.GetMimeMapping(filePath),
                    orgSlug, internalRef, files, null, FileDescriptorType.Single,_logger, _ddbManager);
            }
            // Otherwise we zip everything together and return the package
            else
            {
                streamDescriptor = new FileStreamDescriptor($"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                    "application/zip", orgSlug, internalRef, files, folders,
                    includeDdb ? FileDescriptorType.Dataset : FileDescriptorType.Multiple, _logger, _ddbManager);
            }

            return streamDescriptor;
        }
        
        // private async Task<FileDescriptorStreamDto> GetOfflineFileDescriptor(string orgSlug, string dsSlug, Guid internalRef,
        //     string[] paths)
        // {
        //     var ddb = _ddbManager.Get(orgSlug, internalRef);
        //
        //     var (files, folders, includeDdb) = GetFilePaths(paths, ddb);
        //
        //     FileDescriptorStreamDto descriptorStream;
        //
        //     // If there is just one file we return it
        //     if (files.Length == 1 && paths?.Length == 1 && files[0] == paths[0])
        //     {
        //         var filePath = files.First();
        //
        //         _logger.LogInformation($"Only one path found: '{filePath}'");
        //
        //         var localPath = ddb.GetLocalPath(filePath);
        //
        //         descriptorStream = new FileDescriptorStreamDto
        //         {
        //             ContentStream = File.OpenRead(localPath),
        //             Name = Path.GetFileName(filePath),
        //             ContentType = MimeUtility.GetMimeMapping(filePath)
        //         };
        //
        //     }
        //     // Otherwise we zip everything together and return the package
        //     else
        //     {
        //         descriptorStream = new FileDescriptorStreamDto
        //         {
        //             Name = $"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
        //             ContentStream = new MemoryStream(),
        //             ContentType = "application/zip"
        //         };
        //
        //         using (var archive = new ZipArchive(descriptorStream.ContentStream, ZipArchiveMode.Create, true))
        //         {
        //             foreach (var path in files)
        //             {
        //                 _logger.LogInformation($"Zipping: '{path}'");
        //
        //                 var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        //                 await using var entryStream = entry.Open();
        //
        //                 var localPath = ddb.GetLocalPath(path);
        //                 
        //                 await using var fileStream = File.OpenRead(localPath);
        //                 await fileStream.CopyToAsync(entryStream);
        //
        //             }
        //
        //             // We treat folders separately because if they are empty they would not be included in the archive
        //             if (folders != null)
        //             {
        //                 foreach (var folder in folders)
        //                     archive.CreateEntry(folder + "/");
        //             }
        //
        //             // Include ddb folder
        //             if (includeDdb)
        //             {
        //                 archive.CreateEntryFromAny(Path.Combine(ddb.DatasetFolderPath, ddb.DatabaseFolderName),
        //                     string.Empty, new[] { ddb.BuildFolderPath });
        //             }
        //         }
        //
        //         descriptorStream.ContentStream.Reset();
        //     }
        //
        //     return descriptorStream;
        // }

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
                        tempFiles.AddRange(items.Where(item => item.Type != EntryType.Directory)
                            .Select(item => item.Path));
                        tempFolders.AddRange(items.Where(item => item.Type == EntryType.Directory)
                            .Select(item => item.Path));
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


        public async Task<FileDescriptorStreamDto> GetDdb(string orgSlug, string dsSlug)
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

                return new FileDescriptorStreamDto
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

        public async Task Build(string orgSlug, string dsSlug, string path, bool background = false, bool force = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Build('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to build dataset");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var entry = await ddb.GetEntryAsync(path);

            // Checking if path exists
            if (entry == null)
                throw new InvalidOperationException($"Cannot find source entry: '{path}'");

            // Nothing to do here
            if (!await ddb.IsBuildableAsync(entry.Path))
            {
                _logger.LogInformation($"'{entry.Path}' is not buildable, nothing to do here");
                return;
            }

            if (background)
            {
                
                _logger.LogInformation("Building '{path}' asynchronously", path);

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildWrapper(ddb, path, force, null));

                _logger.LogInformation("Background job id is " + jobId);
 
            }
            else
            {
                _logger.LogInformation("Building '{path}' synchronously", path);

                HangfireUtils.BuildWrapper(ddb, path, force, null);
            }
        }

        #region Build

        public async Task<string> GetBuildFile(string orgSlug, string dsSlug, string hash,
            string path)
        {
            _logger.LogInformation($"In GetBuildFile('{orgSlug}/{dsSlug}', '{hash}', '{path}')");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            EnsureNoWildcardOrEmptyPaths(path);

            Debug.Assert(path != null, nameof(path) + " != null");
            if (Path.IsPathRooted(path) || path.Contains(".."))
                throw new ArgumentException("Rooted or relative paths are not supported");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            
            var destPath = CommonUtils.SafeCombine(BuildBasePath, hash, path);
            
            _logger.LogInformation($"Getting object '{destPath}'");

            var localPath = ddb.GetLocalPath(destPath);

            return Path.GetFullPath(localPath);

        }

        // Base build folder path (example: .ddb/build)
        private string BuildBasePath =>
            CommonUtils.SafeCombine(_ddbManager.DatabaseFolderName, _ddbManager.BuildFolderName);

        public async Task<bool> CheckBuildFile(string orgSlug, string dsSlug, string hash, string path)
        {
            _logger.LogInformation($"In CheckBuildFile('{orgSlug}/{dsSlug}', '{hash}', '{path}')");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            EnsureNoWildcardOrEmptyPaths(path);

            Debug.Assert(path != null, nameof(path) + " != null");
            if (Path.IsPathRooted(path) || path.Contains(".."))
                throw new ArgumentException("Rooted or relative paths are not supported");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            
            var destPath = CommonUtils.SafeCombine(BuildBasePath, hash, path);

            return _fs.Exists(ddb.GetLocalPath(destPath));
        }

        public string GetBuildSource(DdbEntry entry)
        {
            var path = entry.Type switch
            {
                EntryType.PointCloud => CommonUtils.SafeCombine(BuildBasePath, entry.Hash, "ept", "ept.json"),
                EntryType.GeoRaster => CommonUtils.SafeCombine(BuildBasePath, entry.Hash, "cog", "cog.tif"),
                _ => entry.Path
            };

            return path;
        }

        #endregion
    }
}