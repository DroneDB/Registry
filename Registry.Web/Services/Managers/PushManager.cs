using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class PushManager : IPushManager
    {
        private const string AddsTempFolder = "add";
        private const string StampFileName = "stamp.json";
        private const string OurStampFileName = "our_stamp.json";
        private const string MetaFile = "meta.json";

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

        public async Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug, true);

            var validateChecksum = false;

            if (ds is null)
            {
                _logger.LogInformation("Dataset does not exist, creating it");
                await _datasetsManager.AddNew(orgSlug, new DatasetNewDto
                {
                    Name = dsSlug,
                    Slug = dsSlug
                });

                _logger.LogInformation("New dataset {OrgSlug}/{DsSlug} created", orgSlug, dsSlug);
                ds = await _utils.GetDataset(orgSlug, dsSlug);

            }
            else
            {
                if (!await _authManager.IsOwnerOrAdmin(ds))
                    throw new UnauthorizedException("The current user is not allowed to init push");

                validateChecksum = true;
            }

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            Stamp ourStamp = null;

            if (validateChecksum)
            {
                if (string.IsNullOrEmpty(checksum))
                    throw new InvalidOperationException("Checksum parameter missing (dataset exists)");

                // Is a pull required? The checksum passed by client is the checksum of the stamp
                // of the last sync. If it's different, the client should pull first.
                ourStamp = DDBWrapper.GetStamp(ddb.DatasetFolderPath);
                if (ourStamp.Checksum != checksum)
                {
                    return new PushInitResultDto
                    {
                        PullRequired = true
                    };
                }
            }

            ourStamp ??= DDBWrapper.GetStamp(ddb.DatasetFolderPath);

            // Perform delta with our ddb
            var delta = DDBWrapper.Delta(new Stamp
            {
                Checksum = stamp.Checksum, 
                Entries = stamp.Entries,
                Meta = stamp.Meta
            }, ourStamp);

            // Compute locals
            var locals = DDBWrapper.ComputeDeltaLocals(delta, ddb.DatasetFolderPath);

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
                    .Where(item => item.Hash.Length > 0 && !locals.ContainsKey(item.Hash))
                    .Select(item => item.Path)
                    .ToArray(),
                NeededMeta = delta.MetaAdds.ToArray(),
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

        public async Task SaveMeta(string orgSlug, string dsSlug, string token, string meta)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to upload to this dataset");

            // Check if user has enough space to upload this
            await _utils.CheckCurrentUserStorage(meta.Length);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var baseTempFolder = ddb.GetTmpFolder("push-" + token);

            if (!Directory.Exists(baseTempFolder))
                throw new InvalidOperationException("Cannot save meta before initializing push");

            var metaFile = Path.Combine(baseTempFolder, MetaFile);

            // Validate
            JsonConvert.DeserializeObject<List<MetaDump>>(meta);

            // Save file
            await File.WriteAllTextAsync(metaFile, meta);
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
            var metaFile = Path.Combine(baseTempFolder, MetaFile);

            // Check push folder integrity
            if (!File.Exists(stampFilePath))
                throw new InvalidOperationException("Stamp not found");

            if (!File.Exists(ourStampFilePath))
                throw new InvalidOperationException("Our stamp not found");

            var stamp = JsonConvert.DeserializeObject<Stamp>(await File.ReadAllTextAsync(stampFilePath));
            var ourStamp = JsonConvert.DeserializeObject<Stamp>(await File.ReadAllTextAsync(ourStampFilePath));

            if (ourStamp == null)
                throw new InvalidOperationException("Our stamp is invalid (cannot deserialize)");

            // Check that our stamp has not changed! If it has, another client
            // might have performed changes that could conflict with our operation
            // TODO: we could check for conflicts rather than failing and continue
            // the operation if no conflicts are detected.

            var currentStamp = DDBWrapper.GetStamp(ddb.DatasetFolderPath);
            if (currentStamp.Checksum != ourStamp.Checksum)
            {
                throw new InvalidOperationException("The dataset has been changed by another user while pushing. Please try again!");
            }

            // Recompute delta
            var delta = DDBWrapper.Delta(stamp, currentStamp);

            // Create hard links for local files
            var _ = DDBWrapper.ComputeDeltaLocals(delta, ddb.DatasetFolderPath, addTempFolder);

            foreach (var add in delta.Adds.Where(item => item.Hash.Length > 0))
                if (!File.Exists(Path.Combine(addTempFolder, add.Path)))
                    throw new InvalidOperationException($"Cannot commit: missing '{add.Path}'");

            // Read meta dump
            string metaDump = null;
            if (File.Exists(metaFile))
            {
                metaDump = File.ReadAllText(metaFile);
            }

            // Applies delta 
            var conflicts = DDBWrapper.ApplyDelta(delta, addTempFolder, ddb.DatasetFolderPath, MergeStrategy.KeepTheirs, metaDump);

            if (conflicts.Count > 0)
            {
                // This should never happen, since we merge conflicts using keep theirs
                throw new InvalidOperationException("Merge conflicts detected, try pulling first.");
            }

            // Delete temp folder
            Directory.Delete(baseTempFolder, true);

            // Build items
            foreach (var item in delta.Adds)
            {
                if (await ddb.IsBuildableAsync(item.Path))
                {
                    _backgroundJob.Enqueue(() =>
                        HangfireUtils.BuildWrapper(ddb, item.Path, false, null));
                }
            }

            if (await ddb.IsBuildPendingAsync())
            {
                _logger.LogInformation("Items are pending build, retriggering build");

                var jobId = _backgroundJob.Enqueue(() => HangfireUtils.BuildPendingWrapper(ddb, null));

                _logger.LogInformation("Background job id is {JobId}", jobId);
            }
        }
    }
}