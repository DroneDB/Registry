using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Stamp = Registry.Ports.DroneDB.Stamp;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Offline-first push workflow: init, upload files, save meta, commit.
/// </summary>
public class PushManager : IPushManager
{
    private const string AddsTempFolder = "add";
    private const string MetaFile = "meta.json";

    private readonly IUtils _utils;
    private readonly IDdbManager _ddbManager;
    private readonly IObjectsManager _objectsManager;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IAuthManager _authManager;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly ILogger<PushManager> _logger;
    private readonly AppSettings _settings;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly ICacheManager _cacheManager;
    private readonly IFileSystem _fileSystem;
    private readonly BuildPendingService _buildPendingService;
    private readonly ImportSettings _importSettings;
    private readonly IPushSessionStore _sessionStore;

    public PushManager(IUtils utils, IDdbManager ddbManager,
        IObjectsManager objectsManager, ILogger<PushManager> logger, IDatasetsManager datasetsManager,
        IAuthManager authManager, IDdbWrapper ddbWrapper,
        IBackgroundJobsProcessor backgroundJob, IOptions<AppSettings> settings, ICacheManager cacheManager,
        IFileSystem fileSystem, BuildPendingService buildPendingService, IPushSessionStore sessionStore)
    {
        _utils = utils;
        _ddbManager = ddbManager;
        _objectsManager = objectsManager;
        _logger = logger;
        _datasetsManager = datasetsManager;
        _authManager = authManager;
        _ddbWrapper = ddbWrapper;
        _backgroundJob = backgroundJob;
        _settings = settings.Value;
        _cacheManager = cacheManager;
        _fileSystem = fileSystem;
        _buildPendingService = buildPendingService;
        _importSettings = settings.Value.Import ?? new ImportSettings();
        _sessionStore = sessionStore;
    }

    public async Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug, true);

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
            ds = _utils.GetDataset(orgSlug, dsSlug);

        }
        else
        {
            if (!await _authManager.RequestAccess(ds, AccessType.Write))
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
            ourStamp = _ddbWrapper.GetStamp(ddb.DatasetFolderPath);
            if (ourStamp.Checksum != checksum)
            {
                return new PushInitResultDto
                {
                    PullRequired = true
                };
            }
        }

        ourStamp ??= _ddbWrapper.GetStamp(ddb.DatasetFolderPath);

        // Perform delta with our ddb
        var delta = _ddbWrapper.Delta(new Stamp
        {
            Checksum = stamp.Checksum,
            Entries = stamp.Entries,
            Meta = stamp.Meta
        }, ourStamp);

        // Compute locals
        var locals = _ddbWrapper.ComputeDeltaLocals(delta, ddb.DatasetFolderPath);

        // Generate UUID
        var uuid = Guid.NewGuid().ToString();

        // Commit applies this delta if the dataset is still at ourStamp
        await _sessionStore.SaveAsync(GetPushFolder(ddb, uuid), new PushSession
        {
            OurChecksum = ourStamp.Checksum,
            Delta = delta
        });

        // Return missing files list (excluding folders)
        return new PushInitResultDto
        {
            Token = uuid,
            NeededFiles =
            [
                .. delta.Adds
                    .Where(item => item.Hash.Length > 0 && !locals.ContainsKey(item.Hash))
                    .Select(item => item.Path)
            ],
            NeededMeta = [.. delta.MetaAdds],
            PullRequired = false
        };
    }

    public async Task<PushUpload> StreamUploadAsync(string orgSlug, string dsSlug, Stream source,
        long? expectedBytes)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to upload to this dataset");

        // Check if user has enough space to upload this: before writing when the size is known
        if (expectedBytes is { } expected)
            await _utils.CheckCurrentUserStorage(expected);

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        var (tempPath, bytes) = await DatasetUploads.StreamToTempAsync(_fileSystem, ddb.DatasetFolderPath, source);

        if (expectedBytes is null)
        {
            try
            {
                await _utils.CheckCurrentUserStorage(bytes);
            }
            catch
            {
                _fileSystem.Delete(tempPath);
                throw;
            }
        }

        return new PushUpload(ddb, tempPath, bytes);
    }

    public void Upload(PushUpload upload, string path, string token)
    {
        ArgumentNullException.ThrowIfNull(upload);

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty");

        var baseTempFolder = GetPushFolder(upload.Ddb, token);

        if (!_sessionStore.Exists(baseTempFolder))
            throw new InvalidOperationException("Cannot upload file before initializing push");

        var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);
        CommonUtils.ValidateRelativePath(path, addTempFolder);

        // Move the streamed file (same volume) to its place in the push folder
        var filePath = Path.Combine(addTempFolder, path);
        _fileSystem.EnsureParentFolderExists(filePath);
        if (_fileSystem.Exists(filePath))
            _fileSystem.Delete(filePath);
        _fileSystem.Move(upload.TempPath, filePath);
    }

    public async Task SaveMeta(string orgSlug, string dsSlug, string token, string meta)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to upload to this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        var baseTempFolder = GetPushFolder(ddb, token);

        if (!_sessionStore.Exists(baseTempFolder))
            throw new InvalidOperationException("Cannot save meta before initializing push");

        var metaFile = Path.Combine(baseTempFolder, MetaFile);

        // Validate
        JsonConvert.DeserializeObject<List<MetaDump>>(meta);

        // Save file
        await _fileSystem.WriteAllTextAsync(metaFile, meta);
    }

    public async Task Commit(string orgSlug, string dsSlug, string token)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to commit to this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        var baseTempFolder = GetPushFolder(ddb, token);
        var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);
        var metaFile = Path.Combine(baseTempFolder, MetaFile);

        var session = await _sessionStore.LoadAsync(baseTempFolder) ??
                      throw new InvalidOperationException("Push session not found, please push again");

        // Check that the dataset has not changed since Init! If it has, another client
        // might have performed changes that could conflict with our operation
        // TODO: we could check for conflicts rather than failing and continue
        // the operation if no conflicts are detected.
        if (_ddbWrapper.GetStampChecksum(ddb.DatasetFolderPath) != session.OurChecksum)
        {
            throw new InvalidOperationException("The dataset has been changed by another user while pushing. Please try again!");
        }

        // Unchanged dataset: the delta computed by Init still holds
        var delta = session.Delta;

        // Create hard links for local files
        _ddbWrapper.ComputeDeltaLocals(delta, ddb.DatasetFolderPath, addTempFolder);

        foreach (var add in delta.Adds.Where(item => item.Hash.Length > 0))
            if (!_fileSystem.Exists(Path.Combine(addTempFolder, add.Path)))
                throw new InvalidOperationException($"Cannot commit: missing '{add.Path}'");

        // Extension policy gate - check all files being committed
        var blockedFiles = delta.Adds
            .Where(add => !_importSettings.IsExtensionAllowed(add.Path))
            .Select(add => add.Path)
            .ToArray();

        if (blockedFiles.Length > 0)
            throw new ExtensionBlockedException(blockedFiles, _importSettings);

        // Read meta dump
        string metaDump = null;
        if (_fileSystem.Exists(metaFile))
        {
            metaDump = await _fileSystem.ReadAllTextAsync(metaFile);
        }

        // Applies delta
        var conflicts = _ddbWrapper.ApplyDelta(delta, addTempFolder, ddb.DatasetFolderPath, MergeStrategy.KeepTheirs, metaDump);

        if (conflicts.Count > 0)
        {
            // This should never happen, since we merge conflicts using keep theirs
            throw new InvalidOperationException("Merge conflicts detected, try pulling first.");
        }

        // Delete temp folder
        _fileSystem.FolderDelete(baseTempFolder);

        // Build items
        var user = await _authManager.GetCurrentUser();
        var files = delta.Adds.Where(item => item.Hash.Length > 0).ToArray();
        var statuses = ddb.GetBuildStatus([.. files.Select(item => item.Path)]);
        var hasBuildableFiles = false;

        // One status per path, in input order
        foreach (var (item, status) in files.Zip(statuses))
        {
            if (!status.Buildable) continue;

            hasBuildableFiles = true;
            var meta = new IndexPayload(orgSlug, dsSlug, item.Hash, user.Id, null, item.Path);
            _backgroundJob.EnqueueIndexed(() =>
                HangfireUtils.BuildWrapper(ddb, item.Path, false, null), meta);
        }

        // If we enqueued any builds, mark dataset as having pending
        if (hasBuildableFiles)
            await _buildPendingService.SetDatasetHasPendingBuilds(orgSlug, dsSlug, true);

        if (ddb.IsBuildPending())
        {
            _logger.LogInformation("Items are pending build, retriggering build");

            var meta = new IndexPayload(orgSlug, dsSlug, null, user.Id);
            var jobId = _backgroundJob.EnqueueIndexed(() => HangfireUtils.BuildPendingWrapper(ddb, null), meta);

            _logger.LogInformation("Background job id is {JobId}", jobId);
        }
    }

    // Tokens are the GUIDs generated by Init: anything else could point outside the tmp folder
    private static string GetPushFolder(IDDB ddb, string token)
    {
        if (!Guid.TryParse(token, out _))
            throw new ArgumentException("Invalid push token");

        return ddb.GetTmpFolder(IDDB.PushFolderPrefix + token);
    }
}