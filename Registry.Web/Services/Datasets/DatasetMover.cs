#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Datasets;

/// <summary>Moves datasets between organizations (admin): folder, record, listings; a slug taken in the destination is resolved by the strategy.</summary>
public sealed class DatasetMover
{
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly DatasetCommands _commands;
    private readonly IDdbManager _ddbManager;
    private readonly IFileSystem _fileSystem;
    private readonly IDatasetListingInvalidator _listings;
    private readonly ILogger<DatasetMover> _logger;

    public DatasetMover(RegistryContext context, IDatasetResolver datasets, DatasetCommands commands, IDdbManager ddbManager, IFileSystem fileSystem, IDatasetListingInvalidator listings, ILogger<DatasetMover> logger)
    {
        _context = context;
        _datasets = datasets;
        _commands = commands;
        _ddbManager = ddbManager;
        _fileSystem = fileSystem;
        _listings = listings;
        _logger = logger;
    }

    public async Task<IEnumerable<MoveDatasetResultDto>> MoveToOrganization(
        string sourceOrgSlug,
        string[] datasetSlugs,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceOrgSlug))
            throw new BadRequestException("Source organization slug is required");

        if (string.IsNullOrWhiteSpace(destOrgSlug))
            throw new BadRequestException("Destination organization slug is required");

        if (datasetSlugs == null || datasetSlugs.Length == 0)
            throw new BadRequestException("At least one dataset slug is required");

        if (sourceOrgSlug == destOrgSlug)
            throw new BadRequestException("Source and destination organizations cannot be the same");

        // The token is honoured while the move is prepared; the per-dataset moves then run to completion
        var sourceOrg = await _datasets.GetOrganizationAsync(sourceOrgSlug, withTracking: true, ct: ct);
        var destOrg = await _datasets.GetOrganizationAsync(destOrgSlug, withTracking: true, ct: ct);

        var results = new List<MoveDatasetResultDto>();

        // Get existing datasets in destination for conflict detection
        var existingDestDatasets = await _context.Datasets
            .AsNoTracking()
            .Where(d => d.Organization.Slug == destOrgSlug)
            .Select(d => d.Slug)
            .ToListAsync(ct);

        foreach (var dsSlug in datasetSlugs)
        {
            var result = new MoveDatasetResultDto
            {
                OriginalSlug = dsSlug,
                NewSlug = dsSlug
            };

            try
            {
                var dataset = await _datasets.GetDatasetAsync(sourceOrgSlug, dsSlug, safe: true, withTracking: true, CancellationToken.None);

                if (dataset == null)
                {
                    result.Success = false;
                    result.Error = $"Dataset '{dsSlug}' not found in organization '{sourceOrgSlug}'";
                    results.Add(result);
                    continue;
                }

                // Its queued and running jobs name it by the old organization and slug (the same rule as the slug repair)
                await _context.EnsureNoActiveJobsAsync(sourceOrg.Slug, dataset.Slug, CancellationToken.None);

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
                            await _commands.Delete(destOrgSlug, dsSlug, CancellationToken.None);
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
                var sourcePath = _ddbManager.GetDatasetFolder(sourceOrgSlug, dataset.InternalRef);
                var destPath = _ddbManager.GetDatasetFolder(destOrgSlug, dataset.InternalRef);

                if (_fileSystem.FolderExists(sourcePath))
                {
                    // The copy then the delete of the source need its index files closed
                    _ddbManager.ReleaseConnections(sourcePath);
                    _logger.LogInformation("Copying dataset files from '{SourcePath}' to '{DestPath}'", sourcePath, destPath);

                    // Copy all files and subdirectories
                    _fileSystem.FolderCopy(sourcePath, destPath);
                }

                // Update the database record
                var storedSlug = dataset.Slug;
                dataset.Organization = destOrg;
                dataset.Slug = targetSlug;

                // Its job history follows it, saved with the record
                await _context.FollowDatasetAsync(sourceOrg.Slug, storedSlug, destOrg.Slug, targetSlug, CancellationToken.None);

                await _context.SaveChangesAsync(CancellationToken.None);

                // Add to existing list to avoid future conflicts in this batch
                existingDestDatasets.Add(targetSlug);

                // Delete source files after successful database update
                if (_fileSystem.FolderExists(sourcePath))
                {
                    _logger.LogInformation("Deleting source files at '{SourcePath}'", sourcePath);
                    _fileSystem.FolderDelete(sourcePath);
                }

                // Invalidate caches
                await _listings.ListingChangedAsync(sourceOrgSlug, dataset.InternalRef, CancellationToken.None);

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
