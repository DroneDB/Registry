using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

public class DatasetsManager : IDatasetsManager
{
    private readonly RegistryContext _context;
    private readonly IUtils _utils;
    private readonly ILogger<DatasetsManager> _logger;
    private readonly IObjectsManager _objectsManager;
    private readonly IStacManager _stacManager;
    private readonly IDdbManager _ddbManager;
    private readonly IAuthManager _authManager;
    private readonly ICacheManager _cacheManager;
    private readonly IFileSystem _fileSystem;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly AppSettings _settings;

    public DatasetsManager(
        RegistryContext context,
        IUtils utils,
        ILogger<DatasetsManager> logger,
        IObjectsManager objectsManager,
        IStacManager stacManager,
        IDdbManager ddbManager,
        IAuthManager authManager,
        ICacheManager cacheManager,
        IFileSystem fileSystem,
        IBackgroundJobsProcessor backgroundJob,
        IOptions<AppSettings> settings)
    {
        _context = context;
        _utils = utils;
        _logger = logger;
        _objectsManager = objectsManager;
        _stacManager = stacManager;
        _ddbManager = ddbManager;
        _authManager = authManager;
        _cacheManager = cacheManager;
        _fileSystem = fileSystem;
        _backgroundJob = backgroundJob;
        _settings = settings.Value;
    }

    public async Task<IEnumerable<DatasetDto>> List(string orgSlug)
    {
        var org = _utils.GetOrganization(orgSlug);

        if (!await _authManager.RequestAccess(org, AccessType.Read))
            throw new UnauthorizedException("The current user cannot access this organization");

        var datasets = org.Datasets.ToArray();

        // Optimization C: Short-circuit for owner/admin - they can read all datasets
        var isOwnerOrAdmin = await _authManager.IsOwnerOrAdmin(org);

        // Optimization D: Prefetch Organization.Users once to avoid N lazy loads
        if (!isOwnerOrAdmin && org.Users == null)
            await _context.Entry(org).Collection(o => o.Users).LoadAsync();

        var result = new List<DatasetDto>();

        foreach (var ds in datasets)
        {
            DatasetPermissionsDto permissions;

            if (isOwnerOrAdmin)
            {
                // Owner/admin has full access - skip permission check
                permissions = new DatasetPermissionsDto
                {
                    CanRead = true,
                    CanWrite = true,
                    CanDelete = true
                };
            }
            else
            {
                // Check if user can read this dataset before including it
                permissions = await _authManager.GetDatasetPermissions(ds);
                if (!permissions.CanRead)
                    continue;
            }

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var info = ddb.GetInfo();

            var dto = new DatasetDto
            {
                Slug = ds.Slug,
                CreationDate = ds.CreationDate,
                Properties = info.Properties,
                Size = info.Size,
                Permissions = permissions
            };

            result.Add(dto);
        }

        return result;
    }

    public async Task<DatasetDto> Get(string orgSlug, string dsSlug)
    {
        var dataset = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(dataset, AccessType.Read))
            throw new UnauthorizedException("The current user cannot access this dataset");

        var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

        var dto = dataset.ToDto(ddb.GetInfo());
        dto.Permissions = await _authManager.GetDatasetPermissions(dataset);

        return dto;
    }

    public async Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug)
    {
        var dataset = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(dataset, AccessType.Read))
            throw new UnauthorizedException("The current user cannot access this dataset");

        var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

        var info = ddb.GetInfo();
        info.Depth = 0;
        info.Path = _utils.GenerateDatasetUrl(dataset, true);

        var dto = info.ToDto();

        // Add permissions to properties
        if (dto.Properties == null)
            dto.Properties = new Dictionary<string, object>();

        var permissions = await _authManager.GetDatasetPermissions(dataset);
        dto.Properties["permissions"] = new
        {
            canRead = permissions.CanRead,
            canWrite = permissions.CanWrite,
            canDelete = permissions.CanDelete
        };

        return [dto];
    }

    public async Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset)
    {
        var org = _utils.GetOrganization(orgSlug, withTracking: true);

        if (!await _authManager.RequestAccess(org, AccessType.Write))
            throw new UnauthorizedException("The current user cannot add datasets to this organization");

        if (dataset == null)
            throw new BadRequestException("Dataset is null");

        if (dataset.Slug == null)
            throw new BadRequestException("Dataset slug is null");

        if (!dataset.Slug.IsValidSlug())
            throw new BadRequestException("Dataset slug is invalid");

        if (_context.Datasets.AsNoTracking()
            .Any(item => item.Slug == dataset.Slug && item.Organization.Slug == orgSlug))
            throw new BadRequestException("Dataset with this slug already exists");

        var ds = new Dataset
        {
            Slug = dataset.Slug,
            Organization = org,
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.Now
        };

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        var meta = ddb.Meta.GetSafe();

        meta.Name = string.IsNullOrWhiteSpace(dataset.Name) ? dataset.Slug : dataset.Name;

        if (dataset.Visibility.HasValue)
            meta.Visibility = dataset.Visibility.Value;

        org.Datasets.Add(ds);

        await _context.SaveChangesAsync();

        return ds.ToDto(ddb.GetInfo());
    }

    public async Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset)
    {
        if (dataset == null)
            throw new BadRequestException("Dataset is null");

        var ds = _utils.GetDataset(orgSlug, dsSlug, withTracking: true);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user cannot edit this dataset");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        var meta = ddb.Meta.GetSafe();

        if (dataset.Visibility.HasValue)
        {
            meta.Visibility = dataset.Visibility.Value;

            // Invalidate visibility cache
            await _cacheManager.RemoveAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                orgSlug,
                orgSlug,
                ds.InternalRef,
                _ddbManager
            );

            await _stacManager.ClearCache(ds);
        }

        if (!string.IsNullOrWhiteSpace(dataset.Name))
            meta.Name = dataset.Name;

        await _context.SaveChangesAsync();
    }


    public async Task Delete(string orgSlug, string dsSlug)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug, withTracking: true);

        if (!await _authManager.RequestAccess(ds, AccessType.Delete))
            throw new UnauthorizedException("The current user cannot delete this dataset");

        // Save references before removing from DB
        var internalRef = ds.InternalRef;

        // Remove from database immediately (fast operation)
        _context.Datasets.Remove(ds);
        await _context.SaveChangesAsync();

        // Clear STAC cache
        await _stacManager.ClearCache(ds);

        // Schedule background cleanup job for: cancelling active jobs, removing JobIndex entries, deleting filesystem
        string? jobId = null;
        try
        {
            jobId = _backgroundJob.Enqueue<DatasetCleanupService>(
                service => service.CleanupDeletedDatasetAsync(orgSlug, dsSlug, internalRef, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enqueue cleanup job for dataset {OrgSlug}/{DsSlug} with internalRef {InternalRef}",
                orgSlug, dsSlug, internalRef);
        }

        if (string.IsNullOrEmpty(jobId))
        {
            _logger.LogError(
                "Cleanup job could not be scheduled for dataset {OrgSlug}/{DsSlug} with internalRef {InternalRef}. " +
                "Orphaned folder cleanup will handle this later.",
                orgSlug, dsSlug, internalRef);
        }
        else
        {
            _logger.LogInformation(
                "Dataset {OrgSlug}/{DsSlug} removed from DB, cleanup job scheduled with JobId {JobId}",
                orgSlug, dsSlug, jobId);
        }
    }

    public async Task Rename(string orgSlug, string dsSlug, string newSlug)
    {
        if (string.IsNullOrWhiteSpace(newSlug))
            throw new ArgumentException("New slug is empty");

        if (dsSlug == MagicStrings.DefaultDatasetSlug || newSlug == MagicStrings.DefaultDatasetSlug)
            throw new ArgumentException("Cannot move default dataset");

        if (!newSlug.IsValidSlug())
            throw new ArgumentException($"Invalid slug '{newSlug}'");

        if (dsSlug == newSlug) return; // Nothing to do

        if (_utils.GetDataset(orgSlug, newSlug, true) != null)
            throw new ArgumentException($"Dataset '{newSlug}' already exists");

        var ds = _utils.GetDataset(orgSlug, dsSlug, withTracking: true);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user cannot rename this dataset");

        ds.Slug = newSlug;

        await _context.SaveChangesAsync();
    }

    [Obsolete("Use meta")]
    public async Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug,
        AttributesDto attributes)
    {
        if (attributes == null)
            throw new BadRequestException("Attributes are null");

        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user is not allowed to change attributes");

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        var meta = ddb.Meta.GetSafe();
        meta.IsPublic = attributes.IsPublic;

        // Invalidate visibility cache (changing IsPublic may indirectly modify visibility)
        await _cacheManager.RemoveAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            orgSlug,
            orgSlug,
            ds.InternalRef,
            _ddbManager
        );

        await _stacManager.ClearCache(ds);

        return new Dictionary<string, object> { { "public", attributes.IsPublic } };
    }

    public async Task<StampDto> GetStamp(string orgSlug, string dsSlug)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user cannot access this dataset");

        return _ddbManager.Get(orgSlug, ds.InternalRef).GetStamp().ToDto();
    }

    public async Task<IEnumerable<MoveDatasetResultDto>> MoveToOrganization(
        string sourceOrgSlug,
        string[] datasetSlugs,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict)
    {
        // Only admins can perform this operation
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only administrators can move datasets between organizations");

        if (string.IsNullOrWhiteSpace(sourceOrgSlug))
            throw new BadRequestException("Source organization slug is required");

        if (string.IsNullOrWhiteSpace(destOrgSlug))
            throw new BadRequestException("Destination organization slug is required");

        if (datasetSlugs == null || datasetSlugs.Length == 0)
            throw new BadRequestException("At least one dataset slug is required");

        if (sourceOrgSlug == destOrgSlug)
            throw new BadRequestException("Source and destination organizations cannot be the same");

        var sourceOrg = _utils.GetOrganization(sourceOrgSlug, withTracking: true);
        var destOrg = _utils.GetOrganization(destOrgSlug, withTracking: true);

        var results = new List<MoveDatasetResultDto>();

        // Get existing datasets in destination for conflict detection
        var existingDestDatasets = await _context.Datasets
            .AsNoTracking()
            .Where(d => d.Organization.Slug == destOrgSlug)
            .Select(d => d.Slug)
            .ToListAsync();

        foreach (var dsSlug in datasetSlugs)
        {
            var result = new MoveDatasetResultDto
            {
                OriginalSlug = dsSlug,
                NewSlug = dsSlug
            };

            try
            {
                var dataset = _utils.GetDataset(sourceOrgSlug, dsSlug, safe: true, withTracking: true);

                if (dataset == null)
                {
                    result.Success = false;
                    result.Error = $"Dataset '{dsSlug}' not found in organization '{sourceOrgSlug}'";
                    results.Add(result);
                    continue;
                }

                // Check for slug conflict in destination
                var targetSlug = dsSlug;
                var conflictExists = existingDestDatasets.Contains(dsSlug, StringComparer.OrdinalIgnoreCase);

                if (conflictExists)
                {
                    switch (conflictResolution)
                    {
                        case ConflictResolutionStrategy.HaltOnConflict:
                            result.Success = false;
                            result.Error = $"Dataset '{dsSlug}' already exists in destination organization '{destOrgSlug}'";
                            results.Add(result);
                            continue;

                        case ConflictResolutionStrategy.Overwrite:
                            // Delete existing dataset in destination
                            _logger.LogInformation("Overwriting existing dataset '{DsSlug}' in organization '{DestOrgSlug}'", dsSlug, destOrgSlug);
                            await Delete(destOrgSlug, dsSlug);
                            existingDestDatasets.Remove(dsSlug);
                            break;

                        case ConflictResolutionStrategy.Rename:
                            // Generate a unique name
                            var counter = 1;
                            targetSlug = $"{dsSlug}_{counter}";
                            while (existingDestDatasets.Contains(targetSlug, StringComparer.OrdinalIgnoreCase))
                            {
                                counter++;
                                targetSlug = $"{dsSlug}_{counter}";
                            }
                            result.NewSlug = targetSlug;
                            _logger.LogInformation("Renaming dataset '{DsSlug}' to '{TargetSlug}' to avoid conflict", dsSlug, targetSlug);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(conflictResolution), conflictResolution, null);
                    }
                }

                // Move the physical files using copy + delete strategy
                var sourcePath = Path.Combine(_settings.DatasetsPath, sourceOrgSlug, dataset.InternalRef.ToString());
                var destPath = Path.Combine(_settings.DatasetsPath, destOrgSlug, dataset.InternalRef.ToString());

                if (_fileSystem.FolderExists(sourcePath))
                {
                    _logger.LogInformation("Copying dataset files from '{SourcePath}' to '{DestPath}'", sourcePath, destPath);

                    // Copy all files and subdirectories
                    _fileSystem.FolderCopy(sourcePath, destPath);
                }

                // Update the database record
                dataset.Organization = destOrg;
                dataset.Slug = targetSlug;

                await _context.SaveChangesAsync();

                // Add to existing list to avoid future conflicts in this batch
                existingDestDatasets.Add(targetSlug);

                // Delete source files after successful database update
                if (_fileSystem.FolderExists(sourcePath))
                {
                    _logger.LogInformation("Deleting source files at '{SourcePath}'", sourcePath);
                    _fileSystem.FolderDelete(sourcePath, true);
                }

                // Invalidate caches
                await _stacManager.ClearCache(dataset);

                result.Success = true;
                _logger.LogInformation("Successfully moved dataset '{DsSlug}' from '{SourceOrgSlug}' to '{DestOrgSlug}'",
                    dsSlug, sourceOrgSlug, destOrgSlug);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving dataset '{DsSlug}' from '{SourceOrgSlug}' to '{DestOrgSlug}'",
                    dsSlug, sourceOrgSlug, destOrgSlug);
                result.Success = false;
                result.Error = ex.Message;
            }

            results.Add(result);
        }

        return results;
    }
}