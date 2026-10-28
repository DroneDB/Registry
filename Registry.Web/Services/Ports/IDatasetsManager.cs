#nullable disable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Dataset CRUD interface (list, get, add, edit, delete, rename, move).
/// </summary>
public interface IDatasetsManager
{
    public Task<IEnumerable<DatasetDto>> List(string orgSlug, bool includeThumbnailCheck = false, CancellationToken ct = default);

    /// <summary>
    /// A page of the datasets the current user can read, searched and sorted by the server; the
    /// thumbnail check runs for the datasets of the page only.
    /// </summary>
    /// <returns>The page and the number of datasets that match the search.</returns>
    public Task<(IReadOnlyList<DatasetDto> Items, int Total)> ListPage(string orgSlug, Models.DatasetListQuery query,
        bool includeThumbnailCheck = false, CancellationToken ct = default);
    public Task<DatasetDto> Get(string orgSlug, string dsSlug, CancellationToken ct = default);
    public Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug, CancellationToken ct = default);
    public Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset, CancellationToken ct = default);
    public Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset, CancellationToken ct = default);
    public Task Delete(string orgSlug, string dsSlug, CancellationToken ct = default);

    public Task Rename(string orgSlug, string dsSlug, string newSlug, CancellationToken ct = default);
    Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, AttributesDto attributes, CancellationToken ct = default);
    public Task<StampDto> GetStamp(string orgSlug, string dsSlug, CancellationToken ct = default);

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
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict, CancellationToken ct = default);
}