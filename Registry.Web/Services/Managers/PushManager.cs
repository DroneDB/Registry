using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DDB.Bindings;
using DDB.Bindings.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Web.CodeGeneration;
using Newtonsoft.Json;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using EntryType = Registry.Common.EntryType;

namespace Registry.Web.Services.Managers
{
    public class PushManager : IPushManager
    {
        private const string PushFolderName = "push";
        private const string DdbTempFolder = "ddb";
        private const string AddsTempFolder = "add";
        private const string DeltaFileName = "delta.json";

        private readonly IUtils _utils;
        private readonly IDdbManager _ddbManager;
        private readonly IObjectsManager _objectsManager;
        private readonly IDatasetsManager _datasetsManager;
        private readonly IAuthManager _authManager;
        private readonly IObjectSystem _objectSystem;
        private readonly ILogger<PushManager> _logger;
        private readonly AppSettings _settings;
        private readonly IBackgroundJobsProcessor _backgroundJob;


        public PushManager(IUtils utils, IDdbManager ddbManager, IObjectSystem objectSystem,
            IObjectsManager objectsManager, ILogger<PushManager> logger, IDatasetsManager datasetsManager, IAuthManager authManager, 
            IBackgroundJobsProcessor backgroundJob, IOptions<AppSettings> settings)
        {
            _utils = utils;
            _ddbManager = ddbManager;
            _objectSystem = objectSystem;
            _objectsManager = objectsManager;
            _logger = logger;
            _datasetsManager = datasetsManager;
            _authManager = authManager;
            _backgroundJob = backgroundJob;
            _settings = settings.Value;
        }

        public async Task<PushInitResultDto> Init(string orgSlug, string dsSlug, Stream stream)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug, true);

            if (ds == null)
            {
                _logger.LogInformation("Dataset does not exist, creating it");
                await _datasetsManager.AddNew(orgSlug, new DatasetDto
                {
                    Name = dsSlug,
                    Slug = dsSlug
                });

                _logger.LogInformation($"New dataset {orgSlug}/{dsSlug} created");
                ds = await _utils.GetDataset(orgSlug, dsSlug);
            }
            else
            {
                if (!await _authManager.IsOwnerOrAdmin(ds))
                    throw new UnauthorizedException("The current user is not allowed to push to this dataset");
            }


            // 0) Setup temp folders
            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);
            Directory.CreateDirectory(baseTempFolder);

            var ddbTempFolder = Path.Combine(baseTempFolder, DdbTempFolder);
            Directory.CreateDirectory(ddbTempFolder);

            // 1) Unzip stream contents in temp ddb folder
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(Path.Combine(ddbTempFolder, _ddbManager.DdbFolderName), true);

            // 2) Perform delta with our ddb
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var delta = DroneDB.Delta(ddbTempFolder, ddb.DatabaseFolder).ToDto();

            // 3) Save delta json in temp folder
            await File.WriteAllTextAsync(Path.Combine(baseTempFolder, DeltaFileName),
                JsonConvert.SerializeObject(delta));

            // 4) Return missing files list (excluding folders)
            return new PushInitResultDto
            {
                NeededFiles = delta.Adds
                    .Where(item => item.Type != Common.EntryType.Directory)
                    .Select(item => item.Path)
                    .ToArray()
            };
        }

        public async Task Upload(string orgSlug, string dsSlug, string path, Stream stream)
        {

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty");

            if (stream == null || !stream.CanRead)
                throw new ArgumentException("Stream is null or is not readable");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to upload to this dataset");

            if (path.Contains(".."))
                throw new InvalidOperationException("Path cannot contain dot notation");

            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);

            if (!Directory.Exists(baseTempFolder))
                throw new InvalidOperationException("Cannot upload file before initializing push");

            var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);
            Directory.CreateDirectory(addTempFolder);

            // Calculate new file path
            var filePath = Path.Combine(addTempFolder, path);

            // Ensure subfolder exists
            var parentPath = Path.GetDirectoryName(filePath);
            if (parentPath != null) Directory.CreateDirectory(parentPath);

            // Save file in temp folder
            await using var file = File.OpenWrite(filePath);
            await stream.CopyToAsync(file);

        }

        public async Task Commit(string orgSlug, string dsSlug)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to commit to this dataset");

            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);
            var deltaFilePath = Path.Combine(baseTempFolder, DeltaFileName);
            var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);
            var ddbTempFolder = Path.Combine(baseTempFolder, DdbTempFolder);

            // Check push folder integrity (delta and files)
            if (!File.Exists(deltaFilePath))
                throw new InvalidOperationException("Delta not found");

            var delta = JsonConvert.DeserializeObject<DeltaDto>(await File.ReadAllTextAsync(deltaFilePath));

            if (delta == null)
                throw new ArgumentException("Provided delta is not deserializable");

            foreach (var add in delta.Adds.Where(item => item.Type != EntryType.Directory))
                if (!File.Exists(Path.Combine(addTempFolder, add.Path)))
                    throw new InvalidOperationException($"Cannot commit: missing '{add.Path}'");

            var bucketName = _utils.GetBucketName(orgSlug, ds.InternalRef);
            await _objectSystem.EnsureBucketExists(bucketName, _settings.SafeGetLocation(_logger), _logger);
            
            // Applies delta 
            await ApplyDelta(bucketName, delta, addTempFolder);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            foreach (var item in delta.Adds)
            {
                if (item.Type == EntryType.PointCloud)
                {
                    var tempFileName = Path.Combine(addTempFolder, item.Path);
                    HangfireUtils.BuildWrapper(ddb.DatabaseFolder, item.Path, tempFileName, true, null);
                    //var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildWrapper(ddb.DatabaseFolder, item.Path, tempFileName, true, null));

                }
            }

            // Replaces ddb folder
            FolderUtils.Move(ddbTempFolder, ddb.DatabaseFolder);

            // Clean intermediate files
            await Clean(orgSlug, dsSlug);

        }

        public async Task Clean(string orgSlug, string dsSlug)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);

            _logger.LogInformation("Cleaning '" + baseTempFolder + "'");

            if (Directory.Exists(baseTempFolder))
            {
                try
                {
                    Directory.Delete(baseTempFolder, true);
                    _logger.LogInformation("Done");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cannot cleanup, trying with safe delete");

                    CommonUtils.SafeTreeDelete(baseTempFolder);
                }
            }
            else
            {
                _logger.LogInformation("Nothing to clean");
            }
        }

        private async Task ApplyDelta(string bucketName, DeltaDto delta, string addTempFolder)
        {

            const string tempFolderName = ".tmp";

            _logger.LogInformation("Moving copies to temp folder ");

            for (var index = 0; index < delta.Copies.Length; index++)
            {
                var copy = delta.Copies[index];
                _logger.LogInformation(copy.ToString());

                var source = copy.Source;
                var dest = CommonUtils.SafeCombine(tempFolderName, copy.Source);

                EnsureParentFolderExists(dest);

                _logger.LogInformation(
                    $"Changed copy path from {copy.Source} to {dest}");

                await _objectSystem.CopyObjectAsync(bucketName, source, bucketName, dest);

                delta.Copies[index] = new CopyActionDto { Source = dest, Destination = copy.Destination };
            }


            _logger.LogInformation("Working on removes");

            foreach (var rem in delta.Removes)
            {
                _logger.LogInformation(rem.ToString());

                var dest = rem.Path;

                if (rem.Type != EntryType.Directory)
                {

                    _logger.LogInformation("Deleting file");

                    if (await _objectSystem.ObjectExistsAsync(bucketName, dest))
                    {
                        _logger.LogInformation("File exists in dest, deleting it");
                        await _objectSystem.RemoveObjectAsync(bucketName, dest);
                        _logger.LogInformation("File deleted");
                    }
                    else
                    {
                        _logger.LogInformation("File does not exist in dest, nothing to do");
                    }
                }

            }


            _logger.LogInformation("Working on adds");

            foreach (var add in delta.Adds)
            {

                _logger.LogInformation(add.ToString());

                if (add.Type == EntryType.Directory)
                {
                    _logger.LogInformation("Cant do much on directories");
                    continue;
                }

                var source = CommonUtils.SafeCombine(addTempFolder, add.Path);

                _logger.LogInformation("Uploading file");

                await _objectSystem.PutObjectAsync(bucketName, add.Path, source);

            }

            _logger.LogInformation("Working on direct copies");

            foreach (var copy in delta.Copies)
            {
                _logger.LogInformation(copy.ToString());

                var source = copy.Source;
                var dest = copy.Destination;

                if (await _objectSystem.ObjectExistsAsync(bucketName, dest))
                {
                    _logger.LogInformation("Dest file exists, writing shadow");
                    await _objectSystem.CopyObjectAsync(bucketName, dest, bucketName, dest + ".replace");
                }
                else
                {
                    _logger.LogInformation("Dest file does not exist, performing copy");
                    await _objectSystem.CopyObjectAsync(bucketName, source, bucketName, dest);

                }
            }

            _logger.LogInformation("Working on shadow copies");

            foreach (var copy in delta.Copies)
            {
                var dest = copy.Destination;

                if (await _objectSystem.ObjectExistsAsync(bucketName, dest + ".replace"))
                {
                    _logger.LogInformation(copy.ToString());
                    _logger.LogInformation("Shadow file exists, replacing original one");

                    // Basically a move
                    await _objectSystem.CopyObjectAsync(bucketName, dest + ".replace", bucketName, dest);
                    await _objectSystem.RemoveObjectAsync(bucketName, dest + ".replace");

                }
            }

            _logger.LogInformation("Clearing temp folder");

            var objects = _objectSystem.ListObjectsAsync(bucketName, tempFolderName, true).ToEnumerable().ToArray();

            foreach (var obj in objects)
            {
                await _objectSystem.RemoveObjectAsync(bucketName, obj.Key);
            }

        }

        private void EnsureParentFolderExists(string folder)
        {
            var tempFolder = Path.GetDirectoryName(folder);
            if (tempFolder != null) Directory.CreateDirectory(tempFolder);
        }
    }


}