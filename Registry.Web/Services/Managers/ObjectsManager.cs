using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
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
using Registry.Common.Model;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Managers;

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
    private readonly IDdbWrapper _ddbWrapper;
    private readonly IThumbnailGenerator _thumbnailGenerator;
    private readonly IJobIndexQuery _jobIndexQuery;

    // TODO: Could be moved to config
    private const int DefaultThumbnailSize = 512;

    private static bool IsReservedPath(string path)
    {
        return path.StartsWith(IDDB.DatabaseFolderName);
    }

    public ObjectsManager(ILogger<ObjectsManager> logger,
        RegistryContext context,
        IOptions<AppSettings> settings,
        IDdbManager ddbManager,
        IUtils utils,
        IAuthManager authManager,
        ICacheManager cacheManager,
        IFileSystem fs,
        IBackgroundJobsProcessor backgroundJob,
        IDdbWrapper ddbWrapper,
        IThumbnailGenerator thumbnailGenerator,
        IJobIndexQuery jobIndexQuery)
    {
        _logger = logger;
        _context = context;
        _ddbManager = ddbManager;
        _utils = utils;
        _authManager = authManager;
        _cacheManager = cacheManager;
        _fs = fs;
        _backgroundJob = backgroundJob;
        _ddbWrapper = ddbWrapper;
        _settings = settings.Value;
        _thumbnailGenerator = thumbnailGenerator;
        _jobIndexQuery = jobIndexQuery;
    }

    public async Task<IEnumerable<EntryDto>> List(string orgSlug, string dsSlug, string path = null,
        bool recursive = false, EntryType? type = null)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In List('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to list this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        _logger.LogInformation("Searching in '{Path}'", path);

        var entities = ddb.Search(path, recursive);

        if (type != null)
            entities = entities.Where(item => item.Type == type);

        var files = entities.Select(item => item.ToDto()).ToArray();

        _logger.LogInformation("Found {FilesCount} objects", files.Length);

        return files;
    }

    public async Task<IEnumerable<EntryDto>> Search(string orgSlug, string dsSlug, string query = null,
        string path = null, bool recursive = true, EntryType? type = null)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In Search('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to search this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        _logger.LogInformation("Searching in '{Path}' -> {Query} ({Recursive}", path, query, recursive ? 'r' : 'n');

        var entities = ddb.Search(path, recursive);

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
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In Get('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path should not be null");

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read this dataset");

        return await InternalGet(orgSlug, ds.InternalRef, path);
    }

    private async Task<StorageEntryDto> InternalGet(string orgSlug, Guid internalRef, string path)
    {
        var ddb = _ddbManager.Get(orgSlug, internalRef);

        var entry = ddb.GetEntry(path);

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
            ContentType = entry.Path != null ? MimeTypes.GetMimeType(entry.Path) : null,
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
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In AddNew('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to write to this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        // If it's a folder
        if (stream == null)
        {
            if (ddb.EntryExists(path))
                throw new InvalidOperationException("Cannot create a folder on another entry");

            if (path == IDDB.DatabaseFolderName)
                throw new InvalidOperationException($"'{IDDB.DatabaseFolderName}' is a reserved folder name");

            _logger.LogInformation("Adding folder to DDB");

            // Add to DDB
            ddb.Add(path);

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

        var entry = ddb.GetEntry(path);

        if (entry == null)
            throw new InvalidOperationException("Cannot find just added file!");

        _logger.LogInformation("Entry OK");

        var user = await _authManager.GetCurrentUser();

        if (ddb.IsBuildable(entry.Path))
        {
            _logger.LogInformation("This item is buildable, build it!");

            var meta = new IndexPayload(orgSlug, dsSlug, entry.Hash, user.Id, null, entry.Path);
            var jobId = _backgroundJob.EnqueueIndexed(() => HangfireUtils.BuildWrapper(ddb, path, false, null), meta);

            _logger.LogInformation("Background job id is {JobId}", jobId);
        }
        else if (ddb.IsBuildPending())
        {
            _logger.LogInformation("Items are pending build, retriggering build");

            var meta = new IndexPayload(orgSlug, dsSlug, entry.Hash, user.Id, null, entry.Path);
            var jobId = _backgroundJob.EnqueueIndexed(() => HangfireUtils.BuildPendingWrapper(ddb, null), meta);

            _logger.LogInformation("Background job id is {JobId}", jobId);
        }
        else if (entry.Type is EntryType.Image or EntryType.GeoImage)
        {
            _logger.LogInformation("This item is an image, generate thumbnail");

            // Run task in background (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _cacheManager.GetAsync(MagicStrings.ThumbnailCacheSeed, $"{orgSlug}/{dsSlug}", entry.Hash, DefaultThumbnailSize,
                        new Func<Task<byte[]>>(async () =>
                        {
                            _logger.LogDebug("Background thumbnail generation for new file: '{LocalFilePath}'", localFilePath);
                            using var s = new MemoryStream();
                            await _thumbnailGenerator.GenerateThumbnailAsync(localFilePath, DefaultThumbnailSize, s);
                            var result = s.ToArray();
                            _logger.LogDebug("Background generated thumbnail of {Size} bytes for: '{LocalFilePath}'", result.Length, localFilePath);
                            return result;
                        }));
                    _logger.LogInformation("Thumbnail generation completed for hash {Hash}", entry.Hash);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate thumbnail for hash {Hash}, file: '{LocalFilePath}'", entry.Hash, localFilePath);
                }
            });

            _logger.LogInformation("Thumbnail generation task started");
        }

        return entry.ToDto();
    }

    /// <summary>
    /// Validates that the specified entry (file or directory) does not have any active builds.
    /// Throws an exception if any active build is found.
    /// </summary>
    /// <param name="ddb">The DDB instance to check</param>
    /// <param name="entry">The entry to validate</param>
    /// <param name="path">The path of the entry</param>
    /// <exception cref="InvalidOperationException">Thrown when an active build is detected</exception>
    private static async Task ValidateNoBuildActive(IDDB ddb, Entry entry, string path)
    {
        if (entry.Type == EntryType.Directory)
        {
            // For directories, check all files inside
            var allEntries = ddb.Search(path, true);
            var filesWithActiveBuild = allEntries
                .Where(item => item.Type != EntryType.Directory && ddb.IsBuildActive(item.Path))
                .ToArray();

            if (filesWithActiveBuild.Length != 0)
            {
                var filesList = string.Join(", ", filesWithActiveBuild.Select(f => f.Path));
                throw new InvalidOperationException(
                    $"Cannot transfer directory '{path}' because the following files have an active build in progress: {filesList}");
            }
        }
        else
        {
            // For single files, check if build is active
            if (ddb.IsBuildActive(path))
                throw new InvalidOperationException(
                    $"Cannot transfer file '{path}' because it has an active build in progress");
        }
    }

    public async Task Transfer(string sourceOrgSlug, string sourceDsSlug, string sourcePath, string destOrgSlug,
        string destDsSlug, string destPath = null, bool overwrite = false)
    {

        var sourceDs = _utils.GetDataset(sourceOrgSlug, sourceDsSlug);
        var destDs = _utils.GetDataset(destOrgSlug, destDsSlug);

        _logger.LogInformation(
            "In Transfer('{SourceOrgSlug}/{SourceDsSlug}, {SourcePath}' -> '{DestOrgSlug}/{DestDsSlug}', {DestPath}; {Overwrite})",
            sourceOrgSlug, sourceDsSlug, sourcePath, destOrgSlug, destDsSlug, destPath, overwrite);

        if (sourceOrgSlug == destOrgSlug && sourceDsSlug == destDsSlug)
            throw new InvalidOperationException("Source and destination cannot be the same");

        if (!await _authManager.RequestAccess(sourceDs, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read the source dataset");

        if (!await _authManager.RequestAccess(destDs, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to write to the destination dataset");

        var sourceDdb = _ddbManager.Get(sourceOrgSlug, sourceDs.InternalRef);
        var destDdb = _ddbManager.Get(destOrgSlug, destDs.InternalRef);

        var sourceEntry = sourceDdb.GetEntry(sourcePath);

        // Checking if source exists
        if (sourceEntry == null)
            throw new InvalidOperationException("Cannot find source entry: '" + sourcePath + "'");

        // Validate that source has no active build
        await ValidateNoBuildActive(sourceDdb, sourceEntry, sourcePath);

        // If destPath is not specified, use the source file/folder name in the root of the destination dataset
        if (string.IsNullOrWhiteSpace(destPath))
        {
            destPath = Path.GetFileName(sourcePath);
            _logger.LogInformation("Destination path not specified, using source name: '{DestPath}'", destPath);
        }

        // Validate destination path to prevent path traversal attacks

        if (destPath.Contains("..") || Path.IsPathRooted(destPath))
            throw new ArgumentException("Invalid destination path: path traversal or absolute paths are not allowed");

        if (IsReservedPath(destPath))
            throw new InvalidOperationException($"'{destPath}' is a reserved path");

        // Calculate entry size for storage check
        var entrySize = await GetEntrySizeAsync(sourceDdb, sourceEntry);
        _logger.LogInformation("Source entry size: {Size} bytes", entrySize);

        // Check if user has enough storage space in destination
        await _utils.CheckCurrentUserStorage(entrySize);

        var destEntry = destDdb.GetEntry(destPath);

        if (destEntry != null)
        {
            if (!overwrite)
                throw new InvalidOperationException("Destination entry already exists and overwrite is false");

            if (sourceEntry.Type == EntryType.Directory && destEntry.Type != EntryType.Directory)
                throw new ArgumentException("Cannot transfer a folder on a file");

            if (sourceEntry.Type != EntryType.Directory && destEntry.Type == EntryType.Directory)
                throw new ArgumentException("Cannot transfer a file on a folder");
        }

        // Track progress for rollback
        var fileSystemCopied = false;
        var addedToDdb = false;
        var buildFolderCopied = false;
        string destLocalFilePath = null;
        string destBuildPath = null;

        try
        {
            switch (sourceEntry.Type)
            {
                case EntryType.Directory:
                {
                    var sourceLocalFilePath = sourceDdb.GetLocalPath(sourcePath);
                    destLocalFilePath = destDdb.GetLocalPath(destPath);

                    _logger.LogInformation("Transferring directory '{Source}' to '{Dest}'", sourcePath, destPath);

                    CommonUtils.EnsureSafePath(destLocalFilePath);

                    _fs.FolderCopy(sourceLocalFilePath, destLocalFilePath, true);
                    break;

                }
                case EntryType.DroneDB:
                    throw new InvalidOperationException("Cannot transfer a DroneDB file");

                case EntryType.Undefined:
                case EntryType.Generic:
                case EntryType.GeoImage:
                case EntryType.GeoRaster:
                case EntryType.PointCloud:
                case EntryType.Image:
                case EntryType.Markdown:
                case EntryType.Video:
                case EntryType.Geovideo:
                case EntryType.Model:
                case EntryType.Panorama:
                case EntryType.GeoPanorama:
                case EntryType.Vector:
                default:
                {
                    var sourceLocalFilePath = sourceDdb.GetLocalPath(sourcePath);
                    destLocalFilePath = destDdb.GetLocalPath(destPath);

                    _logger.LogInformation("Transferring file '{Source}' to '{Dest}'", sourcePath, destPath);

                    CommonUtils.EnsureSafePath(destLocalFilePath);

                    _fs.Copy(sourceLocalFilePath, destLocalFilePath, true);
                    break;
                }

            }

            fileSystemCopied = true;

            _logger.LogInformation("FS copy OK, performing ddb add");
            destDdb.AddRaw(destDdb.GetLocalPath(destPath));
            addedToDdb = true;

            if (!destDdb.EntryExists(destPath))
                throw new InvalidOperationException(
                    $"Cannot find destination '{destPath}' after transfer, something wrong with ddb");

            _logger.LogInformation("DDB add OK");

            // If it's not a folder, we may need to transfer the build folder (if exists)
            if (sourceEntry.Type != EntryType.Directory)
            {

                // We need to transfer the build folder (if exists), this is an optimization to avoid re-building everything
                var sourceBuildPath = Path.Combine(sourceDdb.BuildFolderPath, sourceEntry.Hash);

                if (_fs.FolderExists(sourceBuildPath))
                {
                    destBuildPath = Path.Combine(destDdb.BuildFolderPath, sourceEntry.Hash);
                    _logger.LogInformation("Transferring build folder '{SourceBuildPath}' to '{DestBuildPath}'",
                        sourceBuildPath, destBuildPath);
                    CommonUtils.EnsureSafePath(destBuildPath);
                    _fs.FolderMove(sourceBuildPath, destBuildPath);
                    buildFolderCopied = true;
                }
                else
                {
                    _logger.LogInformation("No build folder found at '{SourceBuildPath}'", sourceBuildPath);
                }
            }
            else
            {
                // If this is a folder we need to copy all build folders for each file in the folder
                var allEntries = sourceDdb.Search(sourcePath, true);
                var files = allEntries.Where(item => item.Type != EntryType.Directory).ToArray();
                _logger.LogInformation("This is a folder transfer, checking build folders for {FilesCount} files",
                    files.Length);

                foreach (var entry in files)
                {
                    var sourceBuildPath = Path.Combine(sourceDdb.BuildFolderPath, entry.Hash);

                    if (_fs.FolderExists(sourceBuildPath))
                    {
                        var destBuildPathForEntry = Path.Combine(destDdb.BuildFolderPath, entry.Hash);
                        _logger.LogInformation("Transferring build folder '{SourceBuildPath}' to '{DestBuildPath}'",
                            sourceBuildPath, destBuildPathForEntry);
                        CommonUtils.EnsureSafePath(destBuildPathForEntry);
                        _fs.FolderMove(sourceBuildPath, destBuildPathForEntry);
                        buildFolderCopied = true;
                    }
                    else
                    {
                        _logger.LogInformation("No build folder found at '{SourceBuildPath}'", sourceBuildPath);
                    }
                }
            }

            _logger.LogInformation("Removing source file");

            // Remove source file
            sourceDdb.Remove(sourcePath);

            _logger.LogInformation("Transfer OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer failed, attempting rollback");

            // Rollback in reverse order
            if (buildFolderCopied && destBuildPath != null)
            {
                try
                {
                    _logger.LogWarning("Rolling back build folder copy");
                    if (_fs.FolderExists(destBuildPath))
                        Directory.Delete(destBuildPath, true);
                }
                catch (Exception rbEx)
                {
                    _logger.LogError(rbEx, "Failed to rollback build folder copy");
                }
            }

            if (addedToDdb)
            {
                try
                {
                    _logger.LogWarning("Rolling back DDB add operation");
                    destDdb.Remove(destPath);
                }
                catch (Exception rbEx)
                {
                    _logger.LogError(rbEx, "Failed to rollback DDB add");
                }
            }

            if (fileSystemCopied && destLocalFilePath != null)
            {
                try
                {
                    _logger.LogWarning("Rolling back file system copy");
                    if (sourceEntry.Type == EntryType.Directory)
                    {
                        if (_fs.FolderExists(destLocalFilePath))
                            Directory.Delete(destLocalFilePath, true);
                    }
                    else
                    {
                        if (_fs.Exists(destLocalFilePath))
                            _fs.Delete(destLocalFilePath);
                    }
                }
                catch (Exception rbEx)
                {
                    _logger.LogError(rbEx, "Failed to rollback file system copy");
                }
            }

            throw;
        }

    }

    public async Task Move(string orgSlug, string dsSlug, string source, string dest)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In Move('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to write to this dataset");

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

        switch (sourceEntry.Type)
        {
            case EntryType.Directory:
            {
                var sourceLocalFilePath = ddb.GetLocalPath(source);
                var destLocalFilePath = ddb.GetLocalPath(dest);

                _logger.LogInformation("Moving directory '{Source}' to '{Dest}'", source, dest);

                CommonUtils.EnsureSafePath(destLocalFilePath);
                _fs.FolderMove(sourceLocalFilePath, destLocalFilePath);

                break;
            }
            case EntryType.DroneDB:
                throw new InvalidOperationException("Cannot move a DroneDB file");
            case EntryType.Undefined:
            case EntryType.Generic:
            case EntryType.GeoImage:
            case EntryType.GeoRaster:
            case EntryType.PointCloud:
            case EntryType.Image:
            case EntryType.Markdown:
            case EntryType.Video:
            case EntryType.Geovideo:
            case EntryType.Model:
            case EntryType.Panorama:
            case EntryType.GeoPanorama:
            case EntryType.Vector:
            default:
            {
                var sourceLocalFilePath = ddb.GetLocalPath(source);
                var destLocalFilePath = ddb.GetLocalPath(dest);

                _logger.LogInformation("Moving file '{Source}' to '{Dest}'", source, dest);

                CommonUtils.EnsureSafePath(destLocalFilePath);
                _fs.Move(sourceLocalFilePath, destLocalFilePath);

                break;
            }
        }

        _logger.LogInformation("FS move OK");

        _logger.LogInformation("Performing ddb move");
        ddb.Move(source, dest);

        if (!ddb.EntryExists(dest))
            throw new InvalidOperationException(
                $"Cannot find destination '{dest}' after move, something wrong with ddb");

        _logger.LogInformation("Move OK");
    }

    public async Task Delete(string orgSlug, string dsSlug, string path)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In Delete('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to edit this dataset");

        if (IsReservedPath(path))
            throw new InvalidOperationException($"'{path}' is a reserved path");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        if (!ddb.EntryExists(path))
            throw new BadRequestException($"Path '{path}' not found in dataset");

        var objs = ddb.Search(path, true).ToArray();

        // Let's delete from DDB first
        try
        {
            _logger.LogInformation("Removing from DDB");
            ddb.Remove(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cannot delete {ObjPath} from DDB: {Reason}", path, ex.Message);
            throw new InvalidOperationException("Cannot delete object from database", ex);
        }

        var filesToDelete = objs.Where(item => item.Type != EntryType.Directory).ToArray();

        _logger.LogInformation("Deleting {FilesCount} files", filesToDelete.Length);

        // Then we delete from the file system
        foreach (var obj in filesToDelete)
        {
            var objLocalPath = ddb.GetLocalPath(obj.Path);

            _logger.LogInformation("Deleting {ObjPath} in physical path {PhysicalPath}", obj.Path, objLocalPath);

            try
            {
                if (!_fs.Exists(objLocalPath))
                    throw new InvalidOperationException(
                        $"Cannot find local file '{objLocalPath}' for object '{obj.Path}'");

                _fs.Delete(objLocalPath);

            }
            catch (Exception ex)
            {
                // We basically ignore this error, it's not critical. We should perform a cleanup later
                _logger.LogWarning("Cannot delete local file {ObjPath}: {Reason}", objLocalPath, ex.Message);
            }
        }

        _logger.LogInformation("Deletion complete");
    }

    public async Task DeleteAll(string orgSlug, string dsSlug)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In DeleteAll('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to edit this dataset");

        _ddbManager.Delete(orgSlug, ds.InternalRef);
    }

    public async Task<StorageDataDto> GenerateThumbnailData(string orgSlug, string dsSlug, string path, int? sizeRaw,
        bool recreate = false)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In GenerateThumbnailData('{OrgSlug}/{DsSlug}', path: '{Path}', size: {Size}, recreate: {Recreate})",
            orgSlug, dsSlug, path, sizeRaw, recreate);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

        // Fix fox '/img.png' -> 'img.png'
        if (path.StartsWith('/')) path = path[1..];

        var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

        var fileName = Path.GetFileName(path);
        if (fileName == null)
            throw new ArgumentException("Path is not valid");

        var sourcePath = GetBuildSource(entry);
        var localPath = ddb.GetLocalPath(sourcePath);

        var size = sizeRaw ?? DefaultThumbnailSize;


        if (recreate)
        {
            _logger.LogDebug("Removing cached thumbnail for {OrgSlug}/{DsSlug}, hash: {Hash}", orgSlug, dsSlug, entry.Hash);
            await _cacheManager.RemoveAsync(MagicStrings.ThumbnailCacheSeed, $"{orgSlug}/{dsSlug}", entry.Hash);
        }

        var thumbData = await _cacheManager.GetAsync(MagicStrings.ThumbnailCacheSeed, $"{orgSlug}/{dsSlug}", entry.Hash, size,
            new Func<Task<byte[]>>(async () =>
            {
                _logger.LogDebug("Cache miss - generating new thumbnail for: '{LocalPath}'", localPath);
                using var stream = new MemoryStream();
                await _thumbnailGenerator.GenerateThumbnailAsync(localPath, size, stream);
                var result = stream.ToArray();
                _logger.LogDebug("Generated thumbnail of {Size} bytes for: '{LocalPath}'", result.Length, localPath);
                return result;
            }));

        var extension = MimeUtility.GetExtensions(_ddbWrapper.ThumbnailMimeType)?.FirstOrDefault() ?? "webp";

        _logger.LogDebug("Returning thumbnail data: {Size} bytes, type: {ContentType}", thumbData.Length, _ddbWrapper.ThumbnailMimeType);

        return new StorageDataDto
        {
            Name = Path.ChangeExtension(fileName, extension),
            Data = thumbData,
            ContentType = _ddbWrapper.ThumbnailMimeType
        };
    }

    public async Task<StorageDataDto> GenerateTileData(string orgSlug, string dsSlug, string path, int tz, int tx,
        int ty, bool retina)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In GenerateTileData('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

        var fileName = Path.GetFileName(path);
        if (fileName == null)
            throw new ArgumentException("Path is not valid");

        var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

        var sourcePath = GetBuildSource(entry);
        var localPath = ddb.GetLocalPath(sourcePath);

        try
        {
            var tileData =
                await _cacheManager.GetAsync(MagicStrings.TileCacheSeed, $"{orgSlug}/{dsSlug}", entry.Hash, tx, ty, tz, retina,
                    new Func<Task<byte[]>>(() => Task.Run(() => ddb.GenerateTile(localPath, tz, tx, ty, retina, entry.Hash))));

            return new StorageDataDto
            {
                Data = tileData,
                ContentType = "image/webp",
                Name = $"{ty}.webp"
            };
        }
        catch (InvalidOperationException ex)
        {
            // NOTE: This is the definition of self-inflicted wound
            if (ex.InnerException != null &&
                ex.InnerException.Message.Contains("Out of bounds", StringComparison.OrdinalIgnoreCase))
                throw new NotFoundException("Tile out of bounds", ex);

            throw;
        }
    }

    #region Downloads

    public async Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In DownloadStream('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

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
                orgSlug, internalRef, files, null, FileDescriptorType.Single, _logger, _ddbManager, _settings.MaxZipMemoryThreshold);
        }
        // Otherwise we zip everything together and return the package
        else
        {
            streamDescriptor = new FileStreamDescriptor($"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                "application/zip", orgSlug, internalRef, files, folders,
                includeDdb ? FileDescriptorType.Dataset : FileDescriptorType.Multiple, _logger, _ddbManager, _settings.MaxZipMemoryThreshold);
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

                if (entry == null)
                    throw new InvalidOperationException($"Path '{path}' not found in ddb, cannot continue");

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

        if (res == null || res.Length == 0)
            throw new ArgumentException($"Invalid path: '{path}'");

        return res.First();
    }

    private static void EnsureNoWildcardOrEmptyPaths(string path)
    {
        if (path.Contains('*') || path.Contains('?') || string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Wildcards or empty paths are not supported");
    }

    private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths)
    {
        EnsurePathsValidity(orgSlug, internalRef, paths, out _);
    }

    private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths, out IDDB ddb)
    {
        ddb = null;

        if (paths == null || paths.Length == 0)
            // Everything
            return;

        if (paths.Any(path => path.Contains('*') || path.Contains('?') || string.IsNullOrWhiteSpace(path)))
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
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In GetDdb('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

        return new FileStreamDescriptor($"{orgSlug}-{dsSlug}-ddb.zip",
            "application/zip", orgSlug, ds.InternalRef, [], [],
            FileDescriptorType.Dataset, _logger, _ddbManager, _settings.MaxZipMemoryThreshold);
    }

    #region Build

    public async Task Build(string orgSlug, string dsSlug, string path, bool force = false)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In Build('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        // You need write access to build
        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to build dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        var entry = ddb.GetEntry(path);

        // Checking if path exists
        if (entry == null)
            throw new InvalidOperationException($"Cannot find source entry: '{path}'");

        // Nothing to do here
        if (!ddb.IsBuildable(entry.Path))
        {
            _logger.LogInformation("'{EntryPath}' is not buildable, nothing to do here", entry.Path);
            return;
        }
        
        // Let's check if a build is already active
        if (ddb.IsBuildActive(entry.Path))
            throw new InvalidOperationException($"A build is already in progress for '{entry.Path}'");
        
        // Always build asynchronously using background job
        _logger.LogInformation("Building '{Path}' asynchronously", path);

        var user = await _authManager.GetCurrentUser();
        var meta = new IndexPayload(orgSlug, dsSlug, entry.Hash , user.Id, null, path);
        var jobId = _backgroundJob.EnqueueIndexed(() => HangfireUtils.BuildWrapper(ddb, path, force, null), meta);

        _logger.LogInformation("Background job id is {JobId}", jobId);
    }

    public async Task<string> GetBuildFile(string orgSlug, string dsSlug, string hash,
        string path)
    {
        _logger.LogInformation("In GetBuildFile('{OrgSlug}/{DsSlug}', '{Hash}', '{Path}')", orgSlug, dsSlug, hash,
            path);

        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

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
    private static string BuildBasePath =>
        CommonUtils.SafeCombine(IDDB.DatabaseFolderName, IDDB.BuildFolderName);

    public async Task<bool> CheckBuildFile(string orgSlug, string dsSlug, string hash, string path)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In CheckBuildFile('{OrgSlug}/{DsSlug}', '{Hash}', '{Path}')", orgSlug, dsSlug, hash,
            path);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

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
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In GetEntryType('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        var entry = ddb.GetEntry(path);

        return entry?.Type;
    }

    public static string GetBuildSource(Entry entry)
    {
        var path = entry.Type switch
        {
            EntryType.PointCloud => CommonUtils.SafeCombine(BuildBasePath, entry.Hash, "ept", "ept.json"),
            EntryType.GeoRaster => CommonUtils.SafeCombine(BuildBasePath, entry.Hash, "cog", "cog.tif"),
            EntryType.Vector => CommonUtils.SafeCombine(BuildBasePath, entry.Hash, "vec", "vector.fgb"),
            EntryType.Model => CommonUtils.SafeCombine(BuildBasePath, entry.Hash, "nxs", "model.nxz"),
            _ => entry.Path
        };

        return path;
    }

    public async Task<IEnumerable<BuildJobDto>> GetBuilds(string orgSlug, string dsSlug, int page = 1, int pageSize = 50)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        _logger.LogInformation("In GetBuilds('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to read dataset");

        // Convert page/pageSize to skip/take
        var skip = (page - 1) * pageSize;

        var jobIndexes = await _jobIndexQuery.GetByOrgDsAsync(orgSlug, dsSlug, skip, pageSize);

        return jobIndexes.Select(ji => new BuildJobDto
        {
            JobId = ji.JobId,
            Hash = ji.Hash,
            Path = ji.Path,
            CurrentState = ji.CurrentState,
            CreatedAt = DateTime.SpecifyKind(ji.CreatedAtUtc, DateTimeKind.Utc),
            ProcessingAt = ji.ProcessingAtUtc.HasValue ? DateTime.SpecifyKind(ji.ProcessingAtUtc.Value, DateTimeKind.Utc) : null,
            SucceededAt = ji.SucceededAtUtc.HasValue ? DateTime.SpecifyKind(ji.SucceededAtUtc.Value, DateTimeKind.Utc) : null,
            FailedAt = ji.FailedAtUtc.HasValue ? DateTime.SpecifyKind(ji.FailedAtUtc.Value, DateTimeKind.Utc) : null,
            DeletedAt = ji.DeletedAtUtc.HasValue ? DateTime.SpecifyKind(ji.DeletedAtUtc.Value, DateTimeKind.Utc) : null
        });
    }

    /// <summary>
    /// Calculates the total size of an entry (file or directory recursively)
    /// </summary>
    /// <param name="ddb">The DDB instance</param>
    /// <param name="entry">The entry to calculate size for</param>
    /// <returns>Total size in bytes</returns>
    private async Task<long> GetEntrySizeAsync(IDDB ddb, Entry entry)
    {
        if (entry.Type == EntryType.Directory)
        {
            // For directories, we need to calculate the total size of all files recursively
            var localPath = ddb.GetLocalPath(entry.Path);

            if (!Directory.Exists(localPath))
            {
                _logger.LogWarning("Directory not found at '{LocalPath}', returning 0 size", localPath);
                return 0;
            }

            try
            {
                return await Task.Run(() => CommonUtils.GetDirectorySize(localPath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to calculate directory size for '{Path}'", entry.Path);
                throw new InvalidOperationException($"Cannot calculate directory size: {ex.Message}", ex);
            }
        }
        else
        {
            // For files, use the size from the entry
            return entry.Size;
        }
    }

    #endregion
}