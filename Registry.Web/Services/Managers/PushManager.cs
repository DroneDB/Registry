using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Web.CodeGeneration;
using Newtonsoft.Json;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    /*
    public class PushManager : IPushManager
    {
        private const string PushFolderName = "push";
        private const string DdbTempFolder = "ddb";
        private const string AddsTempFolder = "add";
        private const string StampFileName = "stamp.json";
        private const string OurStampFileName = "our_stamp.json";

        private readonly IUtils _utils;
        private readonly IDdbManager _ddbManager;
        private readonly IObjectsManager _objectsManager;
        private readonly IDatasetsManager _datasetsManager;
        private readonly IAuthManager _authManager;
        private readonly ILogger<PushManager> _logger;
        private readonly AppSettings _settings;
        private readonly IBackgroundJobsProcessor _backgroundJob;
        
        public PushManager(IUtils utils, IDdbManager ddbManager, 
            IObjectsManager objectsManager, ILogger<PushManager> logger, IDatasetsManager datasetsManager,
            IAuthManager authManager,
            IBackgroundJobsProcessor backgroundJob, IOptions<AppSettings> settings)
        {
            _utils = utils;
            _ddbManager = ddbManager;
            _objectsManager = objectsManager;
            _logger = logger;
            _datasetsManager = datasetsManager;
            _authManager = authManager;
            _backgroundJob = backgroundJob;
            _settings = settings.Value;
        }

        public async Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, Stamp stamp)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug, true);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to init push");

            bool validateChecksum = ds != null;

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

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            Stamp ourStamp = null;

            if (validateChecksum)
            {
                if (string.IsNullOrEmpty(checksum))
                    throw new InvalidOperationException("Checksum parameter missing (dataset exists)");

                // Is a pull required? The checksum passed by client is the checksum of the stamp
                // of the last sync. If it's different, the client should pull first.
                ourStamp = DroneDB.GetStamp(ddb.DatasetFolderPath);
                if (ourStamp.Checksum != checksum)
                {
                    return new PushInitResultDto
                    {
                        PullRequired = true
                    };
                }
            }

            if (ourStamp == null) ourStamp = DroneDB.GetStamp(ddb.DatasetFolderPath);

            // Perform delta with our ddb
            var delta = DroneDB.Delta(stamp, ourStamp);

            // Generate UUID
            var uuid = Guid.NewGuid().ToString();

            // Create tmp folder
            var baseTempFolder = ddb.GetTmpFolder("push-" + uuid);

            // Save incoming stamp as well as our stamp in temp folder
            await File.WriteAllTextAsync(Path.Combine(baseTempFolder, StampFileName),
                JsonConvert.SerializeObject(stamp));
            await File.WriteAllTextAsync(Path.Combine(baseTempFolder, OurStampFileName),
                            JsonConvert.SerializeObject(ourStamp));
            
            // Return missing files list (excluding folders)
            return new PushInitResultDto
            {
                Token = uuid,
                NeededFiles = delta.Adds
                    .Where(item => item.Hash.Length > 0)
                    .Select(item => item.Path)
                    .ToArray(),
                PullRequired = false
            };
        }

        public async Task Upload(string orgSlug, string dsSlug, string path, string token, Stream stream)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty");

            if (!stream.CanRead)
                throw new ArgumentException("Stream is null or is not readable");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to upload to this dataset");

            // Check if user has enough space to upload this
            await _utils.CheckCurrentUserStorage(stream.Length);

            if (path.Contains(".."))
                throw new InvalidOperationException("Path cannot contain dot notation");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var baseTempFolder = ddb.GetTmpFolder("push-" + token);

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

        public async Task Commit(string orgSlug, string dsSlug, string token)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to commit to this dataset");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var baseTempFolder = ddb.GetTmpFolder("push-" + token);
            var stampFilePath = Path.Combine(baseTempFolder, StampFileName);
            var ourStampFilePath = Path.Combine(baseTempFolder, OurStampFileName);
            var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);

            // Check push folder integrity
            if (!File.Exists(stampFilePath))
                throw new InvalidOperationException("Stamp not found");

            if (!File.Exists(ourStampFilePath))
                throw new InvalidOperationException("Our stamp not found");

            Stamp stamp = JsonConvert.DeserializeObject<Stamp>(await File.ReadAllTextAsync(stampFilePath));
            Stamp ourStamp = JsonConvert.DeserializeObject<Stamp>(await File.ReadAllTextAsync(ourStampFilePath));

            // Check that our stamp has not changed! If it has, another client
            // might have performed changes that could conflict with our operation
            // TODO: we could check for conflicts rather than failing and continue
            // the operation if no conflicts are detected.

            var currentStamp = DroneDB.GetStamp(ddb.DatasetFolderPath);
            if (currentStamp.Checksum != ourStamp.Checksum)
            {
                throw new InvalidOperationException("The dataset has been changed by another user while pushing. Please try again!");
            }

            // Recompute delta
            var delta = DroneDB.Delta(stamp, currentStamp);

            foreach (var add in delta.Adds.Where(item => item.Hash.Length > 0))
                if (!File.Exists(Path.Combine(addTempFolder, add.Path)))
                    throw new InvalidOperationException($"Cannot commit: missing '{add.Path}'");

            // Applies delta 
            var conflicts = DroneDB.ApplyDelta(delta, addTempFolder, ddb.DatasetFolderPath, MergeStrategy.KeepTheirs);

            if (conflicts.Count > 0)
            {
                // This should never happen, since we merge conflicts using keep theirs
                throw new InvalidOperationException("Merge conflicts detected, try pulling first.");
            }

            // Delete temp folder
            Directory.Delete(baseTempFolder, true);

            // TODO: should this be delegated to ddb?
            
            /*
            foreach (var item in delta.Adds)
            {
                var tempFileName = Path.Combine(addTempFolder, item.Path);

                if (await ddb.IsBuildableAsync(item.Path))
                {
                    var jobId = _backgroundJob.Enqueue(() =>
                        HangfireUtils.BuildWrapper(ddb, item.Path, tempFileName, null, true, null));
                    
                    var deleteId = _backgroundJob.ContinueJobWith(jobId, () =>
                        HangfireUtils.SafeDelete(tempFileName, null));

                    var entry = await ddb.GetEntryAsync(item.Path);

                    // Put it on storage
                    var syncId = _backgroundJob.ContinueJobWith(deleteId, () => HangfireUtils.SyncBuildFolder(_objectSystem, ddb, entry, bucketName, null));
                    var buildFolder = Path.Combine(ddb.BuildFolderPath, entry.Hash);
                    _backgroundJob.ContinueJobWith(syncId,
                        () => HangfireUtils.SafeDelete(buildFolder, null));

                }
                else
                {
                    if (!CommonUtils.SafeDelete(tempFileName))
                        _logger.LogWarning($"Cannot delete '{tempFileName}'");
                }
            }*/
        }

        public async Task Clean(string orgSlug, string dsSlug)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);

            _logger.LogInformation($"Cleaning '{baseTempFolder}'");

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

        private static void EnsureParentFolderExists(string folder)
        {
            var tempFolder = Path.GetDirectoryName(folder);
            if (tempFolder != null) Directory.CreateDirectory(tempFolder);
        }
    }*/
}