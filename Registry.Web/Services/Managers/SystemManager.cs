using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Hangfire;

namespace Registry.Web.Services.Managers;

public class SystemManager : ISystemManager
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly ILogger<SystemManager> _logger;
    private readonly IObjectsManager _objectManager;
    private readonly AppSettings _settings;
    private readonly BuildPendingService _buildPendingService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly ICacheManager _cacheManager;
    private readonly IFileSystem _fileSystem;
    private readonly IJobIndexWriter _jobIndexWriter;

    public SystemManager(IAuthManager authManager,
        RegistryContext context, IDdbManager ddbManager, ILogger<SystemManager> logger,
        IObjectsManager objectManager, IOptions<AppSettings> settings, BuildPendingService buildPendingService,
        IHttpClientFactory httpClientFactory, IBackgroundJobsProcessor backgroundJob, ICacheManager cacheManager,
        IFileSystem fileSystem, IJobIndexWriter jobIndexWriter)
    {
        _authManager = authManager;
        _context = context;
        _ddbManager = ddbManager;
        _logger = logger;
        _objectManager = objectManager;
        _settings = settings.Value;
        _buildPendingService = buildPendingService;
        _httpClientFactory = httpClientFactory;
        _backgroundJob = backgroundJob;
        _cacheManager = cacheManager;
        _fileSystem = fileSystem;
        _jobIndexWriter = jobIndexWriter;
    }

    public async Task<CleanupDatasetResultDto> CleanupEmptyDatasets()
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        var datasets = _context.Datasets.Include(ds => ds.Organization).ToArray();

        _logger.LogInformation("Found {DatasetsCount} with objects count zero", datasets.Length);

        var deleted = new List<string>();
        var notDeleted = new List<CleanupDatasetErrorDto>();

        foreach (var ds in datasets)
        {
            _logger.LogInformation("Analyzing dataset {OrgSlug}/{DsSlug}", ds.Organization.Slug, ds.Slug);

            try
            {
                // Check if objects count is ok
                var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

                var entries = ddb.Search("*", true)?.ToArray();

                if (entries == null || !entries.Any())
                {
                    _context.Remove(ds);
                    await _context.SaveChangesAsync();

                    deleted.Add(ds.Slug);
                    _logger.LogInformation("Deleted");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot remove dataset '{DsSlug}'", ds.Slug);
                notDeleted.Add(new CleanupDatasetErrorDto
                {
                    Dataset = ds.Slug,
                    Organization = ds.Organization.Slug,
                    Message = ex.Message
                });
            }
        }

        return new CleanupDatasetResultDto
        {
            RemoveDatasetErrors = notDeleted.ToArray(),
            RemovedDatasets = deleted.ToArray()
        };

    }

    public string GetVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    }

    public async Task<IEnumerable<MigrateVisibilityEntryDTO>> MigrateVisibility()
    {

        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        var query = (from ds in _context.Datasets.Include("Organization")
            select new { ds = ds.Slug, ds.InternalRef, Org = ds.Organization.Slug }).ToArray();

        _logger.LogInformation("Migrating to visibility {DatasetsCount} datasets", query.Length);

        var res = new List<MigrateVisibilityEntryDTO>();

        foreach (var pair in query)
        {
            var ddb = _ddbManager.Get(pair.Org, pair.InternalRef);
            var meta = ddb.Meta.GetSafe();

            if (meta.Visibility.HasValue) continue;

            var isPublic = meta.IsPublic;

            meta.Visibility = isPublic switch
            {
                null => Visibility.Private,
                true => Visibility.Unlisted,
                false => Visibility.Private
            };

            // Invalidate cache after migration
            await _cacheManager.RemoveAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                pair.Org,
                pair.Org,
                pair.InternalRef,
                _ddbManager
            );

            res.Add(new MigrateVisibilityEntryDTO
            {
                IsPublic = isPublic,
                DatasetSlug = pair.ds,
                OrganizationSlug = pair.Org,
                Visibility = meta.Visibility.Value
            });
        }

        return res;
    }

    public async Task<CleanupBatchesResultDto> CleanupBatches()
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        var expiration = DateTime.Now - _settings.UploadBatchTimeout;

        // I'm scared
        var toRemove = (from batch in _context.Batches
                .Include(b => b.Dataset.Organization)
                .Include(b => b.Entries)
            where batch.Status == BatchStatus.Committed ||
                  ((batch.Status == BatchStatus.Rolledback || batch.Status == BatchStatus.Running) &&
                   batch.Entries.Max(entry => entry.AddedOn) < expiration)
            select batch).ToArray();

        var removed = new List<RemovedBatchDto>();
        var errors = new List<RemoveBatchErrorDto>();

        foreach (var batch in toRemove)
        {

            var ds = batch.Dataset;
            var org = ds.Organization;

            try
            {

                // Remove intermediate files
                if (batch.Status is BatchStatus.Rolledback or BatchStatus.Running)
                {

                    var entries = batch.Entries.ToArray();

                    foreach (var entry in entries)
                    {
                        _logger.LogInformation("Deleting '{EntryPath}' of '{OrgSlug}/{DsSlug}'", entry.Path, org.Slug, ds.Slug);
                        await _objectManager.Delete(org.Slug, ds.Slug, entry.Path);
                    }

                    var ddb = _ddbManager.Get(org.Slug, ds.InternalRef);

                    // Remove empty ddb
                    if (!ddb.Search("*", true).Any())
                        _ddbManager.Delete(org.Slug, ds.InternalRef);

                }

                _context.Batches.Remove(batch);

                await _context.SaveChangesAsync();

                removed.Add(new RemovedBatchDto
                {
                    Status = batch.Status,
                    Start = batch.Start,
                    End = batch.End,
                    Token = batch.Token,
                    UserName = batch.UserName,
                    Dataset = ds.Slug,
                    Organization = org.Slug
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot remove batch '{BatchToken}'", batch.Token);
                errors.Add(new RemoveBatchErrorDto
                {
                    Message = ex.Message,
                    Token = batch.Token,
                    Dataset = ds.Slug,
                    Organization = org.Slug
                });
            }
        }

        return new CleanupBatchesResultDto
        {
            RemovedBatches = removed.ToArray(),
            RemoveBatchErrors = errors.ToArray()
        };

    }

    public async Task<BuildPendingStatusDto> GetBuildPendingStatus()
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        return _buildPendingService.GetStatus();
    }

    public async Task<ImportResultDto> ImportDataset(ImportDatasetRequestDto request)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        if (string.IsNullOrWhiteSpace(request.SourceRegistryUrl))
            throw new ArgumentException("SourceRegistryUrl is required");

        ValidateRegistryUrl(request.SourceRegistryUrl);

        if (string.IsNullOrWhiteSpace(request.SourceOrganization))
            throw new ArgumentException("SourceOrganization is required");

        if (string.IsNullOrWhiteSpace(request.SourceDataset))
            throw new ArgumentException("SourceDataset is required");

        var stopwatch = Stopwatch.StartNew();
        var importedItems = new List<ImportedItemDto>();
        var errors = new List<ImportErrorDto>();
        var fileErrors = new List<FileImportErrorDto>();

        try
        {
            // Login to remote registry (if credentials provided)
            string authToken = null;
            if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.Password))
            {
                _logger.LogInformation("Authenticating to {RegistryUrl}", request.SourceRegistryUrl);
                authToken = await AuthenticateRemoteRegistry(request.SourceRegistryUrl, request.Username, request.Password);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    errors.Add(new ImportErrorDto
                    {
                        Organization = request.SourceOrganization,
                        Dataset = request.SourceDataset,
                        Message = "Authentication failed",
                        Phase = ImportPhase.Authentication
                    });

                    return CreateResult(importedItems, errors, fileErrors, stopwatch.Elapsed);
                }
            }
            else
            {
                _logger.LogInformation("No credentials provided, attempting anonymous access to {RegistryUrl}", request.SourceRegistryUrl);
            }

            // Import single dataset using the selected mode
            if (request.Mode == ImportMode.ParallelFiles)
            {
                _logger.LogInformation("Using ParallelFiles import mode");
                await ImportSingleDatasetParallel(
                    request.SourceRegistryUrl,
                    authToken,
                    request.SourceOrganization,
                    request.SourceDataset,
                    request.DestinationOrganization ?? request.SourceOrganization,
                    request.DestinationDataset ?? request.SourceDataset,
                    importedItems,
                    errors,
                    fileErrors
                );
            }
            else
            {
                _logger.LogInformation("Using Archive import mode");
                await ImportSingleDataset(
                    request.SourceRegistryUrl,
                    authToken,
                    request.SourceOrganization,
                    request.SourceDataset,
                    request.DestinationOrganization ?? request.SourceOrganization,
                    request.DestinationDataset ?? request.SourceDataset,
                    importedItems,
                    errors
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing dataset {Org}/{Ds}", request.SourceOrganization, request.SourceDataset);
            errors.Add(new ImportErrorDto
            {
                Organization = request.SourceOrganization,
                Dataset = request.SourceDataset,
                Message = ex.Message,
                Phase = ImportPhase.General
            });
        }

        stopwatch.Stop();
        return CreateResult(importedItems, errors, fileErrors, stopwatch.Elapsed);
    }

    public async Task<ImportResultDto> ImportOrganization(ImportOrganizationRequestDto request)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        if (string.IsNullOrWhiteSpace(request.SourceRegistryUrl))
            throw new ArgumentException("SourceRegistryUrl is required");

        ValidateRegistryUrl(request.SourceRegistryUrl);

        if (string.IsNullOrWhiteSpace(request.SourceOrganization))
            throw new ArgumentException("SourceOrganization is required");

        var stopwatch = Stopwatch.StartNew();
        var importedItems = new List<ImportedItemDto>();
        var errors = new List<ImportErrorDto>();
        var fileErrors = new List<FileImportErrorDto>();

        try
        {
            // Login to remote registry (if credentials provided)
            string authToken = null;
            if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.Password))
            {
                _logger.LogInformation("Authenticating to {RegistryUrl}", request.SourceRegistryUrl);
                authToken = await AuthenticateRemoteRegistry(request.SourceRegistryUrl, request.Username, request.Password);

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    errors.Add(new ImportErrorDto
                    {
                        Organization = request.SourceOrganization,
                        Message = "Authentication failed",
                        Phase = ImportPhase.Authentication
                    });

                    return CreateResult(importedItems, errors, fileErrors, stopwatch.Elapsed);
                }
            }
            else
            {
                _logger.LogInformation("No credentials provided, attempting anonymous access to {RegistryUrl}", request.SourceRegistryUrl);
            }

            // Get list of datasets in organization
            var datasets = await GetRemoteDatasets(request.SourceRegistryUrl, authToken, request.SourceOrganization);

            _logger.LogInformation("Found {Count} datasets in organization {Org}", datasets.Length, request.SourceOrganization);

            // Import each dataset using the selected mode
            _logger.LogInformation("Using {Mode} import mode for organization import", request.Mode);

            foreach (var dataset in datasets)
            {
                try
                {
                    if (request.Mode == ImportMode.ParallelFiles)
                    {
                        await ImportSingleDatasetParallel(
                            request.SourceRegistryUrl,
                            authToken,
                            request.SourceOrganization,
                            dataset.Slug,
                            request.DestinationOrganization ?? request.SourceOrganization,
                            dataset.Slug,
                            importedItems,
                            errors,
                            fileErrors
                        );
                    }
                    else
                    {
                        await ImportSingleDataset(
                            request.SourceRegistryUrl,
                            authToken,
                            request.SourceOrganization,
                            dataset.Slug,
                            request.DestinationOrganization ?? request.SourceOrganization,
                            dataset.Slug,
                            importedItems,
                            errors
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error importing dataset {Org}/{Ds}", request.SourceOrganization, dataset.Slug);
                    errors.Add(new ImportErrorDto
                    {
                        Organization = request.SourceOrganization,
                        Dataset = dataset.Slug,
                        Message = ex.Message,
                        Phase = ImportPhase.General
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing organization {Org}", request.SourceOrganization);
            errors.Add(new ImportErrorDto
            {
                Organization = request.SourceOrganization,
                Message = ex.Message,
                Phase = ImportPhase.General
            });
        }

        stopwatch.Stop();
        return CreateResult(importedItems, errors, fileErrors, stopwatch.Elapsed);
    }

    private async Task<string> AuthenticateRemoteRegistry(string registryUrl, string username, string password)
    {
        var client = _httpClientFactory.CreateClient();

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password)
        ]);

        var response = await client.PostAsync($"{registryUrl.TrimEnd('/')}/users/authenticate", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Authentication failed with status {StatusCode}", response.StatusCode);
            return null;
        }

        var result = await response.Content.ReadAsStringAsync();
        var authResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

        return authResponse?.SafeGetValue("token") as string;
    }

    private async Task<DatasetDto[]> GetRemoteDatasets(string registryUrl, string authToken, string orgSlug)
    {
        var client = _httpClientFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        var response = await client.GetAsync($"{registryUrl.TrimEnd('/')}/orgs/{orgSlug}/ds");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get datasets with status {StatusCode}", response.StatusCode);
            return [];
        }

        var result = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<DatasetDto[]>(result) ?? [];
    }

    /// <summary>
    /// Gets the complete list of files from a remote registry dataset using the search API
    /// </summary>
    private async Task<EntryDto[]> GetRemoteFileListAsync(string registryUrl, string authToken, string orgSlug, string dsSlug)
    {
        var client = _httpClientFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        // Use the search endpoint with recursive flag to get all files
        var searchUrl = $"{registryUrl.TrimEnd('/')}/orgs/{orgSlug}/ds/{dsSlug}/search";

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("query", "*"),
            new KeyValuePair<string, string>("recursive", "true")
        ]);

        var response = await client.PostAsync(searchUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get file list with status {StatusCode}", response.StatusCode);
            return [];
        }

        var result = await response.Content.ReadAsStringAsync();
        var entries = JsonConvert.DeserializeObject<EntryDto[]>(result) ?? [];

        // Filter out directories (we only want files)
        return entries.Where(e => e.Type != EntryType.Directory).ToArray();
    }

    private async Task ImportSingleDataset(
        string registryUrl,
        string authToken,
        string sourceOrg,
        string sourceDs,
        string destOrg,
        string destDs,
        List<ImportedItemDto> importedItems,
        List<ImportErrorDto> errors)
    {
        string tempZipPath = null;

        try
        {
            _logger.LogInformation("Importing dataset {SourceOrg}/{SourceDs} to {DestOrg}/{DestDs}",
                sourceOrg, sourceDs, destOrg, destDs);

            // Download dataset as ZIP to disk (streaming to avoid memory issues)
            tempZipPath = Path.Combine(_settings.TempPath, $"import-{CommonUtils.RandomString(16)}.zip");

            _logger.LogInformation("Downloading dataset to {TempPath}", tempZipPath);

            try
            {
                var downloadUrl = $"{registryUrl.TrimEnd('/')}/orgs/{sourceOrg}/ds/{sourceDs}/download";
                var headers = new Dictionary<string, string>();

                if (!string.IsNullOrWhiteSpace(authToken))
                    headers.Add("Authorization", $"Bearer {authToken}");

                // Progress callback for large downloads
                await HttpHelper.DownloadFileAsync(downloadUrl, tempZipPath, headers, (downloaded, total) =>
                {
                    if (total > 0)
                        _logger.LogDebug("Download progress: {Downloaded:N0} / {Total:N0} bytes ({Percent:P1})",
                            downloaded, total, (double)downloaded / total);
                    else
                        _logger.LogDebug("Download progress: {Downloaded:N0} bytes", downloaded);
                });
            }
            catch (HttpRequestException ex)
            {
                errors.Add(new ImportErrorDto
                {
                    Organization = sourceOrg,
                    Dataset = sourceDs,
                    Message = $"Download failed: {ex.Message}",
                    Phase = ImportPhase.Download
                });
                return;
            }

            var zipFileInfo = new FileInfo(tempZipPath);
            _logger.LogInformation("Downloaded {Size:N0} bytes ({SizeMB:N2} MB)", zipFileInfo.Length, zipFileInfo.Length / 1024.0 / 1024.0);

            // Verify the downloaded file is a valid ZIP (check magic bytes)
            if (!IsValidZipFile(tempZipPath))
            {
                // Try to read content to provide better error message
                var content = await _fileSystem.ReadAllTextAsync(tempZipPath);
                var truncatedContent = content.Length > 500 ? content[..500] + "..." : content;
                _logger.LogWarning("Downloaded file is not a valid ZIP. Content: {Content}", truncatedContent);

                errors.Add(new ImportErrorDto
                {
                    Organization = sourceOrg,
                    Dataset = sourceDs,
                    Message = "Download failed: the server did not return a valid ZIP file. The dataset may be private or require authentication.",
                    Phase = ImportPhase.Download
                });
                return;
            }

            // Get or create destination organization BEFORE extraction
            // This allows us to extract directly to the final destination
            var org = await _context.Organizations.FirstOrDefaultAsync(o => o.Slug == destOrg);
            if (org == null)
            {
                _logger.LogInformation("Creating organization {OrgSlug}", destOrg);
                org = new Organization
                {
                    Slug = destOrg,
                    Name = destOrg,
                    Description = $"Imported from {sourceOrg}",
                    CreationDate = DateTime.UtcNow,
                    IsPublic = false
                };
                _context.Organizations.Add(org);
                await _context.SaveChangesAsync();
            }

            // Get or create destination dataset BEFORE extraction
            var dataset = await _context.Datasets.FirstOrDefaultAsync(d => d.Slug == destDs && d.Organization.Slug == destOrg);
            if (dataset == null)
            {
                _logger.LogInformation("Creating dataset {DsSlug}", destDs);
                dataset = new Dataset
                {
                    Slug = destDs,
                    InternalRef = Guid.NewGuid(),
                    CreationDate = DateTime.UtcNow,
                    Organization = org
                };
                _context.Datasets.Add(dataset);
                await _context.SaveChangesAsync();
            }

            // Get destination path - we'll extract directly here to avoid double copy
            var destPath = Path.Combine(_settings.DatasetsPath, destOrg, dataset.InternalRef.ToString());
            _fileSystem.FolderCreate(destPath);

            _logger.LogInformation("Preparing destination {DestPath}", destPath);

            // Remove all existing content (including .ddb folder if it exists)
            // This is necessary because we want to import the complete .ddb from the source
            var destDirs = _fileSystem.GetDirectories(destPath);
            var destFiles = _fileSystem.GetFiles(destPath);

            if (destDirs.Length > 0 || destFiles.Length > 0)
            {
                _logger.LogInformation("Removing existing dataset content ({DirCount} directories, {FileCount} files)",
                    destDirs.Length, destFiles.Length);
                foreach (var dir in destDirs)
                {
                    _fileSystem.FolderDelete(dir, true);
                }

                foreach (var file in destFiles)
                {
                    _fileSystem.Delete(file);
                }
            }

            // Extract ZIP directly to the final destination (avoids temp folder + move overhead)
            _logger.LogInformation("Extracting ZIP directly to {DestPath}", destPath);
            var extractStartTime = DateTime.UtcNow;

            await Task.Run(() => ExtractZipWithProgress(tempZipPath, destPath));

            var extractDuration = DateTime.UtcNow - extractStartTime;
            _logger.LogInformation("Extraction completed in {Duration:N1} seconds", extractDuration.TotalSeconds);

            // Count files for statistics (after extraction)
            var files = _fileSystem.GetFiles(destPath, "*", SearchOption.AllDirectories);
            var totalSize = files.Sum(f => _fileSystem.GetFileSize(f));

            _logger.LogInformation("Extracted {FileCount} files, total size {Size:N0} bytes ({SizeMB:N2} MB)",
                files.Length, totalSize, totalSize / 1024.0 / 1024.0);

            // Schedule build of the imported dataset to ensure consistency
            _logger.LogInformation("Scheduling build for imported dataset {Org}/{Ds}", destOrg, destDs);
            var ddb = _ddbManager.Get(destOrg, dataset.InternalRef);
            var user = await _authManager.GetCurrentUser();
            var meta = new IndexPayload(destOrg, destDs, null, user.Id, null, null);
            var jobId = _backgroundJob.EnqueueIndexed(() => HangfireUtils.BuildWrapper(ddb, null, true, null), meta);
            _logger.LogInformation("Build scheduled with job id {JobId} for {Org}/{Ds}", jobId, destOrg, destDs);

            importedItems.Add(new ImportedItemDto
            {
                Organization = destOrg,
                Dataset = destDs,
                Size = totalSize,
                FileCount = files.Length,
                ImportedAt = DateTime.UtcNow
            });

            _logger.LogInformation("Successfully imported {Org}/{Ds}", destOrg, destDs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing dataset {Org}/{Ds}", sourceOrg, sourceDs);
            errors.Add(new ImportErrorDto
            {
                Organization = sourceOrg,
                Dataset = sourceDs,
                Message = ex.Message,
                Phase = ImportPhase.Save
            });
        }
        finally
        {
            // Cleanup temporary ZIP file
            try
            {
                if (tempZipPath != null && _fileSystem.Exists(tempZipPath))
                {
                    _logger.LogDebug("Cleaning up temp ZIP file");
                    _fileSystem.Delete(tempZipPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up temporary files");
            }
        }
    }

    /// <summary>
    /// File download task for parallel processing
    /// </summary>
    private class FileDownloadTask
    {
        public EntryDto Entry { get; set; }
        public string DestinationPath { get; set; }
        public string DownloadUrl { get; set; }
    }

    /// <summary>
    /// Result of a file download operation
    /// </summary>
    private class FileDownloadResult
    {
        public EntryDto Entry { get; set; }
        public string LocalPath { get; set; }
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public string ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public long BytesDownloaded { get; set; }
    }

    /// <summary>
    /// Threshold for using file-based streaming instead of memory buffering.
    /// Files larger than this (300MB) are downloaded directly to disk to avoid memory pressure.
    /// </summary>
    private const long LargeFileSizeThreshold = 300 * 1024 * 1024;

    /// <summary>
    /// Imports a single dataset using parallel file downloads.
    /// Files are downloaded in parallel but added to DDB serially (DDB is not thread-safe).
    /// </summary>
    private async Task ImportSingleDatasetParallel(
        string registryUrl,
        string authToken,
        string sourceOrg,
        string sourceDs,
        string destOrg,
        string destDs,
        List<ImportedItemDto> importedItems,
        List<ImportErrorDto> errors,
        List<FileImportErrorDto> fileErrors,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Importing dataset {SourceOrg}/{SourceDs} to {DestOrg}/{DestDs} using parallel file downloads",
                sourceOrg, sourceDs, destOrg, destDs);

            // Get list of files from remote registry
            var remoteFiles = await GetRemoteFileListAsync(registryUrl, authToken, sourceOrg, sourceDs);

            if (remoteFiles.Length == 0)
            {
                _logger.LogWarning("No files found in remote dataset {Org}/{Ds}", sourceOrg, sourceDs);
                errors.Add(new ImportErrorDto
                {
                    Organization = sourceOrg,
                    Dataset = sourceDs,
                    Message = "No files found in remote dataset",
                    Phase = ImportPhase.Download
                });
                return;
            }

            _logger.LogInformation("Found {FileCount} files in remote dataset, total size: {Size:N0} bytes",
                remoteFiles.Length, remoteFiles.Sum(f => f.Size));

            // Get or create destination organization
            var org = await _context.Organizations.FirstOrDefaultAsync(o => o.Slug == destOrg, cancellationToken);
            if (org == null)
            {
                _logger.LogInformation("Creating organization {OrgSlug}", destOrg);
                org = new Organization
                {
                    Slug = destOrg,
                    Name = destOrg,
                    Description = $"Imported from {sourceOrg}",
                    CreationDate = DateTime.UtcNow,
                    IsPublic = false
                };
                _context.Organizations.Add(org);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // Get or create destination dataset
            var dataset = await _context.Datasets.FirstOrDefaultAsync(
                d => d.Slug == destDs && d.Organization.Slug == destOrg, cancellationToken);
            if (dataset == null)
            {
                _logger.LogInformation("Creating dataset {DsSlug}", destDs);
                dataset = new Dataset
                {
                    Slug = destDs,
                    InternalRef = Guid.NewGuid(),
                    CreationDate = DateTime.UtcNow,
                    Organization = org
                };
                _context.Datasets.Add(dataset);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // Get destination path
            var destPath = Path.Combine(_settings.DatasetsPath, destOrg, dataset.InternalRef.ToString());
            _fileSystem.FolderCreate(destPath);

            // Get DDB instance for hash comparison and adding files
            var ddb = _ddbManager.Get(destOrg, dataset.InternalRef);

            // OPTIMIZATION 1: Pre-fetch all existing hashes upfront instead of calling GetEntry for each file
            // This reduces DDB queries from O(n) to O(1) where n is the number of files to import
            _logger.LogInformation("Pre-fetching existing file hashes from DDB...");
            var existingHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var existingEntries = ddb.Search("*", recursive: true);
                foreach (var entry in existingEntries)
                {
                    if (!string.IsNullOrEmpty(entry.Hash))
                    {
                        existingHashes[entry.Path] = entry.Hash;
                    }
                }
                _logger.LogInformation("Found {Count} existing files with hashes", existingHashes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not pre-fetch existing hashes, will check each file individually");
            }

            // Calculate number of parallel workers: half of CPU cores, minimum 2
            var maxParallelDownloads = Math.Max(2, Environment.ProcessorCount / 2);
            _logger.LogInformation("Using {Workers} parallel download workers", maxParallelDownloads);

            // Build headers for download requests
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(authToken))
                headers.Add("Authorization", $"Bearer {authToken}");

            // Statistics (using simple variables since DDB writer is single-threaded)
            var downloadedCount = 0;
            var skippedCount = 0;
            var errorCount = 0;
            long totalBytesDownloaded = 0;
            long totalSkippedBytes = 0;
            var localFileErrors = new List<FileImportErrorDto>();
            var lastProgressLog = DateTime.UtcNow;
            const int progressIntervalSeconds = 10;

            // Create channel for completed downloads that need to be added to DDB
            var completedChannel = Channel.CreateBounded<FileDownloadResult>(new BoundedChannelOptions(maxParallelDownloads * 2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

            // OPTIMIZATION 2 & 3: Use ActionBlock for bounded parallelism without accumulating Task objects
            // This pattern automatically limits concurrent tasks and doesn't hold references to completed tasks
            var downloadBlock = new ActionBlock<FileDownloadTask>(
                async task =>
                {
                    try
                    {
                        // OPTIMIZATION 1 (continued): Use pre-fetched hash dictionary for skip check
                        if (_fileSystem.Exists(task.DestinationPath) &&
                            existingHashes.TryGetValue(task.Entry.Path, out var existingHash) &&
                            existingHash == task.Entry.Hash)
                        {
                            // File exists with same hash, skip download
                            await completedChannel.Writer.WriteAsync(new FileDownloadResult
                            {
                                Entry = task.Entry,
                                LocalPath = task.DestinationPath,
                                Success = true,
                                Skipped = true,
                                BytesDownloaded = 0
                            }, cancellationToken);
                            return;
                        }

                        // OPTIMIZATION 4: For large files, use streaming download to avoid memory pressure
                        // Files larger than LargeFileSizeThreshold (300MB) are downloaded with progress tracking
                        HttpHelper.DownloadResult result;
                        if (task.Entry.Size > LargeFileSizeThreshold)
                        {
                            _logger.LogDebug("Large file detected ({Size:N0} bytes), using streaming download: {Path}",
                                task.Entry.Size, task.Entry.Path);
                        }

                        // Download file with retry (HttpHelper already streams to disk)
                        result = await HttpHelper.DownloadFileWithRetryAsync(
                            task.DownloadUrl,
                            task.DestinationPath,
                            headers,
                            maxRetries: 3,
                            cancellationToken);

                        await completedChannel.Writer.WriteAsync(new FileDownloadResult
                        {
                            Entry = task.Entry,
                            LocalPath = task.DestinationPath,
                            Success = result.Success,
                            Skipped = false,
                            ErrorMessage = result.Success ? null : result.ErrorMessage,
                            RetryCount = result.RetryCount,
                            BytesDownloaded = result.BytesDownloaded
                        }, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await completedChannel.Writer.WriteAsync(new FileDownloadResult
                        {
                            Entry = task.Entry,
                            LocalPath = task.DestinationPath,
                            Success = false,
                            ErrorMessage = ex.Message,
                            BytesDownloaded = 0
                        }, cancellationToken);
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = maxParallelDownloads,
                    BoundedCapacity = maxParallelDownloads * 2,
                    CancellationToken = cancellationToken
                });

            // Producer: post files to download block
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var entry in remoteFiles)
                    {
                        // Skip .ddb folder files - we'll handle metadata separately
                        if (entry.Path.StartsWith(".ddb/") || entry.Path.StartsWith(".ddb\\"))
                            continue;

                        var localPath = Path.Combine(destPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                        var downloadUrl = $"{registryUrl.TrimEnd('/')}/orgs/{sourceOrg}/ds/{sourceDs}/download?path={Uri.EscapeDataString(entry.Path)}&inline=1";

                        await downloadBlock.SendAsync(new FileDownloadTask
                        {
                            Entry = entry,
                            DestinationPath = localPath,
                            DownloadUrl = downloadUrl
                        }, cancellationToken);
                    }
                }
                finally
                {
                    downloadBlock.Complete();
                }
            }, cancellationToken);

            // Wait for downloads to complete, then close the completed channel
            var downloadCompletionTask = Task.Run(async () =>
            {
                await downloadBlock.Completion;
                completedChannel.Writer.Complete();
            }, cancellationToken);

            // DDB writer task: serially add completed downloads to DDB
            var ddbWriterTask = Task.Run(async () =>
            {
                await foreach (var result in completedChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (result.Success)
                    {
                        if (result.Skipped)
                        {
                            skippedCount++;
                            totalSkippedBytes += result.Entry.Size;
                            _logger.LogDebug("Skipped existing file: {Path}", result.Entry.Path);
                        }
                        else
                        {
                            try
                            {
                                // Add file to DDB (this must be serial)
                                ddb.AddRaw(result.LocalPath);
                                downloadedCount++;
                                totalBytesDownloaded += result.BytesDownloaded;

                                if (result.RetryCount > 0)
                                {
                                    _logger.LogDebug("Downloaded file after {Retries} retries: {Path}",
                                        result.RetryCount, result.Entry.Path);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error adding file to DDB: {Path}", result.Entry.Path);
                                errorCount++;
                                localFileErrors.Add(new FileImportErrorDto
                                {
                                    Organization = destOrg,
                                    Dataset = destDs,
                                    FilePath = result.Entry.Path,
                                    Message = $"DDB add failed: {ex.Message}",
                                    RetryCount = result.RetryCount
                                });
                            }
                        }
                    }
                    else
                    {
                        errorCount++;
                        _logger.LogWarning("Failed to download file {Path}: {Error}",
                            result.Entry.Path, result.ErrorMessage);
                        localFileErrors.Add(new FileImportErrorDto
                        {
                            Organization = destOrg,
                            Dataset = destDs,
                            FilePath = result.Entry.Path,
                            Message = result.ErrorMessage,
                            RetryCount = result.RetryCount
                        });
                    }

                    // Log progress periodically based on time, not count
                    if ((DateTime.UtcNow - lastProgressLog).TotalSeconds >= progressIntervalSeconds)
                    {
                        var totalProcessed = downloadedCount + skippedCount + errorCount;
                        _logger.LogInformation("Progress: {Processed}/{Total} files ({Downloaded} downloaded, {Skipped} skipped, {Errors} errors, {Size:N0} bytes)",
                            totalProcessed, remoteFiles.Length, downloadedCount, skippedCount, errorCount, totalBytesDownloaded);
                        lastProgressLog = DateTime.UtcNow;
                    }
                }
            }, cancellationToken);

            // Wait for all tasks to complete
            await Task.WhenAll(producerTask, downloadCompletionTask, ddbWriterTask);

            // Add file errors to the main list
            fileErrors.AddRange(localFileErrors);

            _logger.LogInformation("Import completed: {Downloaded} downloaded, {Skipped} skipped, {Errors} errors, {Size:N0} bytes downloaded, {SkippedSize:N0} bytes skipped",
                downloadedCount, skippedCount, errorCount, totalBytesDownloaded, totalSkippedBytes);

            // Schedule build if we have any files
            if (downloadedCount > 0)
            {
                _logger.LogInformation("Scheduling build for imported dataset {Org}/{Ds}", destOrg, destDs);
                var user = await _authManager.GetCurrentUser();
                var meta = new IndexPayload(destOrg, destDs, null, user.Id, null, null);
                var jobId = _backgroundJob.EnqueueIndexed(() => HangfireUtils.BuildWrapper(ddb, null, true, null), meta);
                _logger.LogInformation("Build scheduled with job id {JobId} for {Org}/{Ds}", jobId, destOrg, destDs);
            }

            // OPTIMIZATION 4: Use already tracked statistics instead of rescanning filesystem
            // This avoids O(n) filesystem operations after import
            var totalSize = totalBytesDownloaded + totalSkippedBytes;
            var totalFileCount = downloadedCount + skippedCount;

            importedItems.Add(new ImportedItemDto
            {
                Organization = destOrg,
                Dataset = destDs,
                Size = totalSize,
                FileCount = totalFileCount,
                SkippedFileCount = skippedCount,
                ImportedAt = DateTime.UtcNow
            });

            _logger.LogInformation("Successfully imported {Org}/{Ds} using parallel downloads", destOrg, destDs);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Import cancelled for {Org}/{Ds}", sourceOrg, sourceDs);
            errors.Add(new ImportErrorDto
            {
                Organization = sourceOrg,
                Dataset = sourceDs,
                Message = "Import was cancelled",
                Phase = ImportPhase.Download
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing dataset {Org}/{Ds} using parallel downloads", sourceOrg, sourceDs);
            errors.Add(new ImportErrorDto
            {
                Organization = sourceOrg,
                Dataset = sourceDs,
                Message = ex.Message,
                Phase = ImportPhase.General
            });
        }
    }

    /// <summary>
    /// Extracts a ZIP file with progress logging for large archives.
    /// Uses buffered extraction for memory efficiency.
    /// </summary>
    private void ExtractZipWithProgress(string zipPath, string destPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var totalEntries = archive.Entries.Count;
        var processedEntries = 0;
        var lastProgressLog = DateTime.UtcNow;
        const int progressIntervalSeconds = 10;

        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destPath, entry.FullName));

            // Security check: ensure the entry doesn't escape the destination directory
            if (!destinationPath.StartsWith(Path.GetFullPath(destPath) + Path.DirectorySeparatorChar))
            {
                _logger.LogWarning("Skipping potentially malicious ZIP entry: {EntryName}", entry.FullName);
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry
                _fileSystem.FolderCreate(destinationPath);
            }
            else
            {
                // File entry - ensure parent directory exists
                _fileSystem.EnsureParentFolderExists(destinationPath);
                entry.ExtractToFile(destinationPath, overwrite: true);
            }

            processedEntries++;

            // Log progress periodically
            if ((DateTime.UtcNow - lastProgressLog).TotalSeconds >= progressIntervalSeconds)
            {
                _logger.LogDebug("Extraction progress: {Processed}/{Total} entries ({Percent:P1})",
                    processedEntries, totalEntries, (double)processedEntries / totalEntries);
                lastProgressLog = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Extraction complete: {Total} entries processed", totalEntries);
    }

    /// <summary>
    /// Checks if a file is a valid ZIP by verifying the magic bytes (PK header)
    /// </summary>
    private bool IsValidZipFile(string filePath)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(filePath);
            if (stream.Length < 4)
                return false;

            var buffer = new byte[4];
            stream.ReadExactly(buffer, 0, 4);

            // ZIP files start with PK (0x50, 0x4B) followed by 0x03, 0x04 or 0x05, 0x06 (empty) or 0x07, 0x08 (spanned)
            return buffer[0] == 0x50 && buffer[1] == 0x4B &&
                   (buffer[2] == 0x03 || buffer[2] == 0x05 || buffer[2] == 0x07);
        }
        catch
        {
            return false;
        }
    }

    private ImportResultDto CreateResult(List<ImportedItemDto> importedItems, List<ImportErrorDto> errors, List<FileImportErrorDto> fileErrors, TimeSpan duration)
    {
        return new ImportResultDto
        {
            ImportedItems = importedItems.ToArray(),
            Errors = errors.ToArray(),
            FileErrors = fileErrors?.ToArray() ?? [],
            TotalSize = importedItems.Sum(i => i.Size),
            TotalFiles = importedItems.Sum(i => i.FileCount),
            SkippedFiles = importedItems.Sum(i => i.SkippedFileCount),
            Duration = duration
        };
    }

    /// <summary>
    /// Validates that the registry URL contains only scheme and host (e.g., https://hub.dronedb.app)
    /// </summary>
    private static void ValidateRegistryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"SourceRegistryUrl is not a valid URL: {url}");

        if (uri.Scheme != "http" && uri.Scheme != "https")
            throw new ArgumentException($"SourceRegistryUrl must use http or https scheme: {url}");

        if (!string.IsNullOrEmpty(uri.PathAndQuery) && uri.PathAndQuery != "/")
            throw new ArgumentException($"SourceRegistryUrl must not contain path or query string, only scheme and host (e.g., https://hub.dronedb.app): {url}");

        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException($"SourceRegistryUrl must not contain fragment: {url}");
    }

    public async Task<RescanResultDto> RescanDatasetIndex(string orgSlug, string dsSlug, string? types = null, bool stopOnError = true)
    {
        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new ArgumentException("Organization slug is required", nameof(orgSlug));

        if (string.IsNullOrWhiteSpace(dsSlug))
            throw new ArgumentException("Dataset slug is required", nameof(dsSlug));

        var dataset = await _context.Datasets
            .Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Organization.Slug == orgSlug && d.Slug == dsSlug);

        if (dataset == null)
            throw new ArgumentException($"Dataset '{orgSlug}/{dsSlug}' not found");

        // Allow admins or users with write access to the dataset
        if (!await _authManager.IsUserAdmin() &&
            !await _authManager.RequestAccess(dataset, AccessType.Write))
            throw new UnauthorizedException("Only admins or users with write access can rescan dataset index");

        _logger.LogInformation("Rescanning index for dataset {OrgSlug}/{DsSlug} with types filter: {Types}",
            orgSlug, dsSlug, types ?? "all");

        var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);
        var results = ddb.RescanIndex(types, stopOnError);

        // Clear build cache (thumbnails, tiles, COGs) since metadata has changed
        _logger.LogInformation("Clearing build cache for dataset {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
        ddb.ClearBuildCache();

        // Clear Redis cache for tiles and thumbnails
        _logger.LogInformation("Clearing cache for dataset {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
        await _objectManager.InvalidateAllDatasetCaches(orgSlug, dsSlug);

        // Enqueue background build jobs to rebuild the entire dataset
        _logger.LogInformation("Enqueueing build jobs for dataset {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
        var user = await _authManager.GetCurrentUser();
        var userId = user?.Id ?? MagicStrings.AutoBuildServiceUserId;
        var meta = new IndexPayload(orgSlug, dsSlug, null, userId, null, null);
        _backgroundJob.EnqueueIndexed(
            () => HangfireUtils.BuildWrapper(ddb, null, true, null), meta);
        _backgroundJob.EnqueueIndexed(
            () => HangfireUtils.BuildPendingWrapper(ddb, null), meta);

        var entries = results.Select(r => new RescanEntryResultDto
        {
            Path = r.Path,
            Hash = r.Hash,
            Success = r.Success,
            Error = r.Error
        }).ToArray();

        _logger.LogInformation("Rescan completed for {OrgSlug}/{DsSlug}: {Total} entries processed, {Success} successful, {Errors} errors",
            orgSlug, dsSlug, entries.Length, entries.Count(e => e.Success), entries.Count(e => !e.Success));

        return new RescanResultDto
        {
            OrganizationSlug = orgSlug,
            DatasetSlug = dsSlug,
            TotalProcessed = entries.Length,
            SuccessCount = entries.Count(e => e.Success),
            ErrorCount = entries.Count(e => !e.Success),
            Entries = entries
        };
    }

    public async Task<CleanupJobIndicesResultDto> CleanupJobIndices(int? retentionDays = null)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        var days = retentionDays ?? _settings.JobIndexRetentionDays;
        if (days <= 0)
            days = 60;

        var cutoff = DateTime.UtcNow.AddDays(-days);

        _logger.LogInformation("Manual JobIndex cleanup: removing terminal records older than {Days} days (before {Cutoff:u})",
            days, cutoff);

        var deleted = await _jobIndexWriter.DeleteTerminalBeforeAsync(cutoff);

        _logger.LogInformation("Manual JobIndex cleanup completed: {Deleted} records removed", deleted);

        return new CleanupJobIndicesResultDto
        {
            DeletedCount = deleted,
            RetentionDays = days
        };
    }
}