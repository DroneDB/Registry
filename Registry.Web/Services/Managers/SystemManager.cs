using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
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
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

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

    public SystemManager(IAuthManager authManager,
        RegistryContext context, IDdbManager ddbManager, ILogger<SystemManager> logger,
        IObjectsManager objectManager, IOptions<AppSettings> settings, BuildPendingService buildPendingService,
        IHttpClientFactory httpClientFactory)
    {
        _authManager = authManager;
        _context = context;
        _ddbManager = ddbManager;
        _logger = logger;
        _objectManager = objectManager;
        _settings = settings.Value;
        _buildPendingService = buildPendingService;
        _httpClientFactory = httpClientFactory;
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

            var attrs = ddb.GetAttributesRaw();

            var isPublic = attrs.SafeGetValue("public");

            meta.Visibility = isPublic switch
            {
                null => Visibility.Private,
                int @public => @public == 1 ? Visibility.Unlisted : Visibility.Private,
                bool @publicBool => @publicBool ? Visibility.Unlisted : Visibility.Private,
                _ => Visibility.Private
            };

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

        if (string.IsNullOrWhiteSpace(request.SourceOrganization))
            throw new ArgumentException("SourceOrganization is required");

        if (string.IsNullOrWhiteSpace(request.SourceDataset))
            throw new ArgumentException("SourceDataset is required");

        var stopwatch = Stopwatch.StartNew();
        var importedItems = new List<ImportedItemDto>();
        var errors = new List<ImportErrorDto>();

        try
        {
            // Login to remote registry
            _logger.LogInformation("Authenticating to {RegistryUrl}", request.SourceRegistryUrl);
            var authToken = await AuthenticateRemoteRegistry(request.SourceRegistryUrl, request.Username, request.Password);

            if (string.IsNullOrWhiteSpace(authToken))
            {
                errors.Add(new ImportErrorDto
                {
                    Organization = request.SourceOrganization,
                    Dataset = request.SourceDataset,
                    Message = "Authentication failed",
                    Phase = "authentication"
                });

                return CreateResult(importedItems, errors, stopwatch.Elapsed);
            }

            // Import single dataset
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing dataset {Org}/{Ds}", request.SourceOrganization, request.SourceDataset);
            errors.Add(new ImportErrorDto
            {
                Organization = request.SourceOrganization,
                Dataset = request.SourceDataset,
                Message = ex.Message,
                Phase = "general"
            });
        }

        stopwatch.Stop();
        return CreateResult(importedItems, errors, stopwatch.Elapsed);
    }

    public async Task<ImportResultDto> ImportOrganization(ImportOrganizationRequestDto request)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        if (string.IsNullOrWhiteSpace(request.SourceRegistryUrl))
            throw new ArgumentException("SourceRegistryUrl is required");

        if (string.IsNullOrWhiteSpace(request.SourceOrganization))
            throw new ArgumentException("SourceOrganization is required");

        var stopwatch = Stopwatch.StartNew();
        var importedItems = new List<ImportedItemDto>();
        var errors = new List<ImportErrorDto>();

        try
        {
            // Login to remote registry
            _logger.LogInformation("Authenticating to {RegistryUrl}", request.SourceRegistryUrl);
            var authToken = await AuthenticateRemoteRegistry(request.SourceRegistryUrl, request.Username, request.Password);

            if (string.IsNullOrWhiteSpace(authToken))
            {
                errors.Add(new ImportErrorDto
                {
                    Organization = request.SourceOrganization,
                    Message = "Authentication failed",
                    Phase = "authentication"
                });

                return CreateResult(importedItems, errors, stopwatch.Elapsed);
            }

            // Get list of datasets in organization
            var datasets = await GetRemoteDatasets(request.SourceRegistryUrl, authToken, request.SourceOrganization);

            _logger.LogInformation("Found {Count} datasets in organization {Org}", datasets.Length, request.SourceOrganization);

            // Import each dataset
            foreach (var dataset in datasets)
            {
                try
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error importing dataset {Org}/{Ds}", request.SourceOrganization, dataset.Slug);
                    errors.Add(new ImportErrorDto
                    {
                        Organization = request.SourceOrganization,
                        Dataset = dataset.Slug,
                        Message = ex.Message,
                        Phase = "general"
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
                Phase = "general"
            });
        }

        stopwatch.Stop();
        return CreateResult(importedItems, errors, stopwatch.Elapsed);
    }

    private async Task<string> AuthenticateRemoteRegistry(string registryUrl, string username, string password)
    {
        var client = _httpClientFactory.CreateClient();

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password)
        });

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
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        var response = await client.GetAsync($"{registryUrl.TrimEnd('/')}/orgs/{orgSlug}/ds");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get datasets with status {StatusCode}", response.StatusCode);
            return Array.Empty<DatasetDto>();
        }

        var result = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<DatasetDto[]>(result) ?? Array.Empty<DatasetDto>();
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
        string tempExtractPath = null;

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
                var headers = new Dictionary<string, string>
                {
                    { "Authorization", $"Bearer {authToken}" }
                };

                await HttpHelper.DownloadFileAsync(downloadUrl, tempZipPath, headers);
            }
            catch (HttpRequestException ex)
            {
                errors.Add(new ImportErrorDto
                {
                    Organization = sourceOrg,
                    Dataset = sourceDs,
                    Message = $"Download failed: {ex.Message}",
                    Phase = "download"
                });
                return;
            }

            var zipFileInfo = new FileInfo(tempZipPath);
            _logger.LogInformation("Downloaded {Size} bytes", zipFileInfo.Length);

            // Extract to temporary folder
            tempExtractPath = Path.Combine(_settings.TempPath, $"import-extract-{CommonUtils.RandomString(16)}");
            Directory.CreateDirectory(tempExtractPath);

            _logger.LogInformation("Extracting to {ExtractPath}", tempExtractPath);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

            // Count files for statistics
            var files = Directory.GetFiles(tempExtractPath, "*", SearchOption.AllDirectories);
            var totalSize = files.Sum(f => new FileInfo(f).Length);

            // Get or create destination organization
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

            // Get or create destination dataset
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

            // Get destination DDB path
            var ddb = _ddbManager.Get(destOrg, dataset.InternalRef);
            var destPath = ddb.DatasetFolderPath;

            _logger.LogInformation("Moving data to {DestPath}", destPath);

            // If destination has existing data, remove it (excluding database folder)
            var destDirs = Directory.GetDirectories(destPath);
            var destFiles = Directory.GetFiles(destPath);

            _logger.LogInformation("Removing existing dataset content");
            foreach (var dir in destDirs)
            {
                if (!dir.Contains(IDDB.DatabaseFolderName))
                    Directory.Delete(dir, true);
            }

            foreach (var file in destFiles)
            {
                File.Delete(file);
            }

            // Move all content from extracted folder to destination using native filesystem operations
            var extractedDirs = Directory.GetDirectories(tempExtractPath);
            var extractedFiles = Directory.GetFiles(tempExtractPath);

            _logger.LogInformation("Moving {DirCount} directories and {FileCount} files",
                extractedDirs.Length, extractedFiles.Length);

            foreach (var dir in extractedDirs)
            {
                var dirName = Path.GetFileName(dir);
                var targetDir = Path.Combine(destPath, dirName);
                Directory.Move(dir, targetDir);
            }

            foreach (var file in extractedFiles)
            {
                var fileName = Path.GetFileName(file);
                var targetFile = Path.Combine(destPath, fileName);
                File.Move(file, targetFile, true);
            }

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
                Phase = "save"
            });
        }
        finally
        {
            // Cleanup temporary files
            try
            {
                if (tempZipPath != null && File.Exists(tempZipPath))
                {
                    _logger.LogDebug("Cleaning up temp ZIP file");
                    File.Delete(tempZipPath);
                }

                if (tempExtractPath != null && Directory.Exists(tempExtractPath))
                {
                    _logger.LogDebug("Cleaning up temp extract folder");
                    Directory.Delete(tempExtractPath, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up temporary files");
            }
        }
    }

    private ImportResultDto CreateResult(List<ImportedItemDto> importedItems, List<ImportErrorDto> errors, TimeSpan duration)
    {
        return new ImportResultDto
        {
            ImportedItems = importedItems.ToArray(),
            Errors = errors.ToArray(),
            TotalSize = importedItems.Sum(i => i.Size),
            TotalFiles = importedItems.Sum(i => i.FileCount),
            Duration = duration
        };
    }
}