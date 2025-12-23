using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface IDatasetsManager
{
    public Task<IEnumerable<DatasetDto>> List(string orgSlug);
    public Task<DatasetDto> Get(string orgSlug, string dsSlug);
    public Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug);
    public Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset);
    public Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset);
    public Task Delete(string orgSlug, string dsSlug);

    public Task Rename(string orgSlug, string dsSlug, string newSlug);
    Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, AttributesDto attributes);
    public Task<StampDto> GetStamp(string orgSlug, string dsSlug);

    /// <summary>
    /// Moves one or more datasets from one organization to another.
    /// This operation requires admin privileges.
    /// </summary>
    /// <param name="sourceOrgSlug">The source organization slug.</param>
    /// <param name="datasetSlugs">Array of dataset slugs to move.</param>
    /// <param name="destOrgSlug">The destination organization slug.</param>
    /// <param name="conflictResolution">How to handle conflicts with existing datasets.</param>
    /// <returns>Results of the move operation for each dataset.</returns>
    public Task<IEnumerable<MoveDatasetResultDto>> MoveToOrganization(
        string sourceOrgSlug,
        string[] datasetSlugs,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict);
}