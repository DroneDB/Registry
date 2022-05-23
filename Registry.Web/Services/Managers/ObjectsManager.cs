using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Registry.Adapters;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Services.Managers
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly ICacheManager _cacheManager;
        private readonly IFileSystem _fs;
        private readonly IBackgroundJobsProcessor _backgroundJob;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        // TODO: Could be moved to config
        private const int DefaultThumbnailSize = 512;

        private bool IsReservedPath(string path)
        {
            return path.StartsWith(DDB.DatabaseFolderName);
        }

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IOptions<AppSettings> settings,
            IDdbManager ddbManager,
            IUtils utils,
            IAuthManager authManager,
            ICacheManager cacheManager,
            IFileSystem fs,
            IBackgroundJobsProcessor backgroundJob)
        {
            _logger = logger;
            _context = context;
            _ddbManager = ddbManager;
            _utils = utils;
            _authManager = authManager;
            _cacheManager = cacheManager;
            _fs = fs;
            _backgroundJob = backgroundJob;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<EntryDto>> List(string orgSlug, string dsSlug, string path = null,
            bool recursive = false, EntryType? type = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In List('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation("Searching in '{Path}'", path);

            var entities = await ddb.SearchAsync(path, recursive);

            if (type != null)
                entities = entities.Where(item => item.Type == type);
            
            var files = entities.Select(item => item.ToDto()).ToArray();

            _logger.LogInformation("Found {FilesCount} objects", files.Length);

            return files;
        }

        public async Task<IEnumerable<EntryDto>> Search(string orgSlug, string dsSlug, string query = null,
            string path = null, bool recursive = true, EntryType? type = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In Search('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation("Searching in '{Path}' -> {Query} ({Recursive}", path, query, recursive ? 'r' : 'n');

            var entities = await ddb.SearchAsync(path, recursive);
            
            if (type != null)
                entities = entities.Where(item => item.Type == type);
            
            var files = (from entry in entities
                let name = Path.GetFileName(entry.Path)
                where FileSystemName.MatchesSimpleExpression(query, name)
                select entry.ToDto()).ToArray();

            _logger.LogInformation("Found {FilesCount} objects", files.Length);

            return files;
        }

        public async Task<StorageEntryDto> Get(string orgSlug, string dsSlug, string path)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In Get('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path should not be null");

            return await InternalGet(orgSlug, ds.InternalRef, path);
        }

        private async Task<StorageEntryDto> InternalGet(string orgSlug, Guid internalRef, string path)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);
            
            var entry = await ddb.GetEntryAsync(path);

            if (entry == null)
                throw new NotFoundException($"Cannot find '{path}'");

            if (entry.Type == EntryType.Directory)
                throw new InvalidOperationException("Cannot get a folder, we are supposed to deal with a file!");

            Debug.Assert(entry.Path != null, "entry.Path != null");
            return new StorageEntryDto
            {
                Hash = entry.Hash,
                Name = Path.GetFileName(entry.Path),
                Size = entry.Size,
                Type = entry.Type,
                ContentType = MimeTypes.GetMimeType(entry.Path),
                PhysicalPath = Path.GetFullPath(ddb.GetLocalPath(entry.Path))
            };
        }

        public async Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data)
        {
            await using var stream = new MemoryStream(data);
            stream.Reset();
            return await AddNew(orgSlug, dsSlug, path, stream);
        }

        public async Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In AddNew('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            // If it's a folder
            if (stream == null)
            {
                if (await ddb.EntryExistsAsync(path))
                    throw new InvalidOperationException("Cannot create a folder on another entry");

                if (path == DDB.DatabaseFolderName)
                    throw new InvalidOperationException($"'{DDB.DatabaseFolderName}' is a reserved folder name");

                _logger.LogInformation("Adding folder to DDB");

                // Add to DDB
                await ddb.AddAsync(path);

                _logger.LogInformation("Added to DDB");

                return new EntryDto
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

            _logger.LogInformation("Local file path is '{LocalFilePath}'", localFilePath);

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

            if (await ddb.IsBuildableAsync(entry.Path))
            {
                _logger.LogInformation("This item is buildable, build it!");

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildWrapper(ddb, path, false, null));

                _logger.LogInformation("Background job id is {JobId}", jobId);
            }else if (await ddb.IsBuildPendingAsync())
            {
                _logger.LogInformation("Items are pending build, retriggering build");

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildPendingWrapper(ddb, null));

                _logger.LogInformation("Background job id is {JobId}", jobId);
            }

            return entry.ToDto();
        }

        public async Task Move(string orgSlug, string dsSlug, string source, string dest)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In Move('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

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

            switch (sourceEntry.Type)
            {
                case EntryType.Directory:
                {
                
                    var sourceLocalFilePath = ddb.GetLocalPath(source);
                    var destLocalFilePath = ddb.GetLocalPath(dest);
                    
                    _logger.LogInformation("Moving directory '{Source}' to '{Dest}'", source, dest);
                
                    CommonUtils.EnsureSafePath(destLocalFilePath);
                    _fs.FolderMove(sourceLocalFilePath, destLocalFilePath);
                    
                    _logger.LogInformation("FS move OK");
                    
                    break;
                }
                case EntryType.DroneDB:
                    throw new InvalidOperationException("Cannot move a DroneDB file");
                default:
                {
                    var sourceLocalFilePath = ddb.GetLocalPath(source);
                    var destLocalFilePath = ddb.GetLocalPath(dest);

                    _logger.LogInformation("Moving file '{Source}' to '{Dest}'", source, dest);
                    
                    CommonUtils.EnsureSafePath(destLocalFilePath);
                    _fs.Move(sourceLocalFilePath, destLocalFilePath);

                    _logger.LogInformation("FS move OK");
                    
                    break;
                }
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

            _logger.LogInformation("In Delete('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            if (IsReservedPath(path))
                throw new InvalidOperationException($"'{path}' is a reserved path");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (!await ddb.EntryExistsAsync(path))
                throw new BadRequestException($"Path '{path}' not found in dataset");

            var objs = (await ddb.SearchAsync(path, true)).ToArray();

            foreach (var obj in objs.Where(item => item.Type != EntryType.Directory))
            {
                _logger.LogInformation("Deleting '{ObjPath}'", obj.Path);

                var objLocalPath = ddb.GetLocalPath(obj.Path);

                try
                {
                    if (!_fs.Exists(objLocalPath))
                        throw new InvalidOperationException(
                            $"Cannot find local file '{objLocalPath}' for object '{obj.Path}'");

                    _fs.Delete(objLocalPath);

                    await _cacheManager.Clear(MagicStrings.ThumbnailCacheSeed, obj.Hash);
                    await _cacheManager.Clear(MagicStrings.TileCacheSeed, obj.Hash);
                }
                catch (Exception ex)
                {
                    // We basically ignore this error, it's not critical. We should perform a cleanup later
                    _logger.LogWarning("Cannot delete local file '{ObjPath}': {Reason}", objLocalPath, ex.Message);
                }
            }

            try
            {
                _logger.LogInformation("Removing from DDB");
                await ddb.RemoveAsync(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Cannot delete '{ObjPath}' from DDB: {Reason}", path, ex.Message);
                throw new InvalidOperationException("Cannot delete object from database", ex);
            }

            _logger.LogInformation("Deletion complete");
        }

        public async Task DeleteAll(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In DeleteAll('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            _ddbManager.Delete(orgSlug, ds.InternalRef);
        }

        public async Task<StorageFileDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size,
            bool recreate = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In GenerateThumbnail('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            // Fix fox '/img.png' -> 'img.png'
            if (path.StartsWith('/')) path = path[1..];

            var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var sourcePath = GetBuildSource(entry);
            var localPath = ddb.GetLocalPath(sourcePath);

            var thumbPath = await _cacheManager.Get(MagicStrings.ThumbnailCacheSeed, entry.Hash, ddb, localPath,
                size ?? DefaultThumbnailSize);

            return new StorageEntryDto
            {
                Name = Path.ChangeExtension(fileName, ".jpg"),
                PhysicalPath = Path.GetFullPath(thumbPath),
                ContentType = "image/jpeg"
            };
        }

        public async Task<StorageFileDto> GenerateTile(string orgSlug, string dsSlug, string path, int tz, int tx,
            int ty, bool retina)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In GenerateTile('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

            var sourcePath = GetBuildSource(entry);
            var localPath = ddb.GetLocalPath(sourcePath);

            try
            {
                var tilePath =
                    await _cacheManager.Get("tile", entry.Hash, ddb, localPath, entry.Hash, tx, ty, tz, retina);

                return new StorageEntryDto
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

        public async Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In DownloadStream('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return GetFileStreamDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

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

                _logger.LogInformation("Only one path found: '{FilePath}'", filePath);

                streamDescriptor = new FileStreamDescriptor(Path.GetFileName(filePath),
                    MimeUtility.GetMimeMapping(filePath),
                    orgSlug, internalRef, files, null, FileDescriptorType.Single, _logger, _ddbManager);
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

        private (string[] files, string[] folders, bool includeDdb) GetFilePaths(string[] paths, IDDB ddb)
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

            _logger.LogInformation("Found {FilesCount} paths", files.Length);
            return (files, folders, includeDdb);
        }

        #endregion

        #region Utils

        private Entry EnsurePathValidity(string orgSlug, Guid internalRef, string path, out IDDB ddb)
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
            EnsurePathsValidity(orgSlug, internalRef, paths, out _);
        }

        private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths, out IDDB ddb)
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


        public async Task<FileStreamDescriptor> GetDdb(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In GetDdb('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            return new FileStreamDescriptor($"{orgSlug}-{dsSlug}-ddb.zip",
                "application/zip", orgSlug, ds.InternalRef, Array.Empty<string>(), Array.Empty<string>(),
                FileDescriptorType.Dataset, _logger, _ddbManager);
        }

        public async Task Build(string orgSlug, string dsSlug, string path, bool background = false, bool force = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In Build('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

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
                _logger.LogInformation("'{EntryPath}' is not buildable, nothing to do here", entry.Path);
                return;
            }

            if (background)
            {
                _logger.LogInformation("Building '{Path}' asynchronously", path);

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildWrapper(ddb, path, force, null));

                _logger.LogInformation("Background job id is {JobId}", jobId);
            }
            else
            {
                _logger.LogInformation("Building '{Path}' synchronously", path);

                HangfireUtils.BuildWrapper(ddb, path, force, null);
            }
        }

        #region Build

        public async Task<string> GetBuildFile(string orgSlug, string dsSlug, string hash,
            string path)
        {
            _logger.LogInformation("In GetBuildFile('{OrgSlug}/{DsSlug}', '{Hash}', '{Path}')", orgSlug, dsSlug, hash,
                path);

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            EnsureNoWildcardOrEmptyPaths(path);

            Debug.Assert(path != null, nameof(path) + " != null");
            if (Path.IsPathRooted(path) || path.Contains(".."))
                throw new ArgumentException("Rooted or relative paths are not supported");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var destPath = CommonUtils.SafeCombine(BuildBasePath, hash, path);

            _logger.LogInformation("Getting object '{DestPath}'", destPath);

            var localPath = ddb.GetLocalPath(destPath);

            return Path.GetFullPath(localPath);
        }

        // Base build folder path (example: .ddb/build)
        private string BuildBasePath =>
            CommonUtils.SafeCombine(DDB.DatabaseFolderName, DDB.BuildFolderName);

        public async Task<bool> CheckBuildFile(string orgSlug, string dsSlug, string hash, string path)
        {
            _logger.LogInformation("In CheckBuildFile('{OrgSlug}/{DsSlug}', '{Hash}', '{Path}')", orgSlug, dsSlug, hash,
                path);

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            EnsureNoWildcardOrEmptyPaths(path);

            Debug.Assert(path != null, nameof(path) + " != null");
            if (Path.IsPathRooted(path) || path.Contains(".."))
                throw new ArgumentException("Rooted or relative paths are not supported");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var destPath = CommonUtils.SafeCombine(BuildBasePath, hash, path);

            return _fs.Exists(ddb.GetLocalPath(destPath));
        }

        public async Task<EntryType?> GetEntryType(string orgSlug, string dsSlug, string path)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In GetEntryType('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);
            
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            
            var entry = await ddb.GetEntryAsync(path);

            return entry?.Type;
        }

        public string GetBuildSource(Entry entry)
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