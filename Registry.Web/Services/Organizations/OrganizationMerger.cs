#nullable disable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Organizations;

/// <summary>Merging an organization into another (out of <c>OrganizationsManager</c>).</summary>
public sealed class OrganizationMerger
{
    private readonly IAuthManager _authManager;
    private readonly OrganizationMembershipService _memberships;
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IDatasetsManager _datasetManager;
    private readonly ILogger<OrganizationMerger> _logger;

    public OrganizationMerger(IAuthManager authManager, OrganizationMembershipService memberships,
        RegistryContext context, IDatasetResolver datasets, IDatasetsManager datasetManager,
        ILogger<OrganizationMerger> logger)
    {
        _authManager = authManager;
        _memberships = memberships;
        _context = context;
        _datasets = datasets;
        _datasetManager = datasetManager;
        _logger = logger;
    }

    /// <summary>
    /// Moves every dataset and member of <paramref name="sourceOrgSlug"/> into <paramref name="destOrgSlug"/> and, when
    /// every dataset moved, deletes the source.
    /// </summary>
    public async Task<MergeOrganizationResultDto> Merge(
        string sourceOrgSlug,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict,
        bool deleteSourceOrganization = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceOrgSlug))
            throw new BadRequestException("Source organization slug is required");

        if (string.IsNullOrWhiteSpace(destOrgSlug))
            throw new BadRequestException("Destination organization slug is required");

        if (sourceOrgSlug == destOrgSlug)
            throw new BadRequestException("Source and destination organizations cannot be the same");

        // Validate public organization cannot be merged
        if (sourceOrgSlug == MagicStrings.PublicOrganizationSlug)
            throw new BadRequestException("Cannot merge the public organization");

        // Honoured while the merge is prepared; the moves then run to completion
        var sourceOrg = await _datasets.GetOrganizationAsync(sourceOrgSlug, withTracking: true, withDatasets: true,
            ct: ct);
        var destOrg = await _datasets.GetOrganizationAsync(destOrgSlug, withTracking: true, ct: ct);

        _logger.LogInformation("Starting merge of organization '{SourceOrgSlug}' into '{DestOrgSlug}'",
            sourceOrgSlug, destOrgSlug);

        var result = new MergeOrganizationResultDto
        {
            SourceOrgSlug = sourceOrgSlug,
            DestinationOrgSlug = destOrgSlug,
            SourceOrganizationDeleted = false
        };

        // Get all datasets from source organization
        var datasetSlugs = sourceOrg.Datasets.Select(d => d.Slug).ToArray();

        if (datasetSlugs.Length > 0)
        {
            // Move all datasets to destination organization
            var moveResults = await _datasetManager.MoveToOrganization(
                sourceOrgSlug,
                datasetSlugs,
                destOrgSlug,
                conflictResolution, CancellationToken.None);

            result.DatasetResults = [.. moveResults];
            result.DatasetsMovedCount = result.DatasetResults.Count(r => r.Success);
            result.DatasetsFailedCount = result.DatasetResults.Count(r => !r.Success);
        }
        else
        {
            result.DatasetResults = [];
            result.DatasetsMovedCount = 0;
            result.DatasetsFailedCount = 0;
        }

        // The members of the source join the destination with their permissions (the source's members
        // were never loaded here, so a merge used to drop every one of them)
        await _memberships.CopyMembersAsync(sourceOrgSlug, destOrgSlug, await _authManager.GetCurrentUser(), CancellationToken.None);

        // Delete source organization if requested and all datasets were moved successfully
        if (deleteSourceOrganization)
        {
            if (result.DatasetsFailedCount == 0)
            {
                _logger.LogInformation("Deleting source organization '{SourceOrgSlug}' after successful merge", sourceOrgSlug);

                // Reload the organization to ensure we have the latest state
                sourceOrg = await _datasets.GetOrganizationAsync(sourceOrgSlug, withTracking: true, ct: CancellationToken.None);

                // Clear remaining users
                sourceOrg.Users?.Clear();

                _context.Organizations.Remove(sourceOrg);
                await _context.SaveChangesAsync(CancellationToken.None);

                result.SourceOrganizationDeleted = true;
            }
            else
            {
                _logger.LogWarning("Not deleting source organization '{SourceOrgSlug}' because some datasets failed to move",
                    sourceOrgSlug);
            }
        }

        _logger.LogInformation("Merge completed: {DatasetsMovedCount} datasets moved, {DatasetsFailedCount} failed, source deleted: {SourceDeleted}",
            result.DatasetsMovedCount, result.DatasetsFailedCount, result.SourceOrganizationDeleted);

        return result;
    }
}
