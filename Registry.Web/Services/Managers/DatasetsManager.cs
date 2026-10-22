using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The datasets API (<see cref="IDatasetsManager"/>, what the controllers and the organizations call) over the
/// dataset services RQ-12 split it into (13 parameters before): queries, commands, the move between organizations.
/// </summary>
public class DatasetsManager : IDatasetsManager
{
    private readonly IDatasetQueries _queries;
    private readonly IDatasetCommands _commands;
    private readonly IDatasetMover _mover;

    public DatasetsManager(IDatasetQueries queries, IDatasetCommands commands, IDatasetMover mover)
    {
        _queries = queries;
        _commands = commands;
        _mover = mover;
    }

    public Task<IEnumerable<DatasetDto>> List(string orgSlug, bool includeThumbnailCheck = false) =>
        _queries.List(orgSlug, includeThumbnailCheck);

    public Task<(IReadOnlyList<DatasetDto> Items, int Total)> ListPage(string orgSlug, DatasetListQuery query,
        bool includeThumbnailCheck = false) => _queries.ListPage(orgSlug, query, includeThumbnailCheck);

    public Task<DatasetDto> Get(string orgSlug, string dsSlug) => _queries.GetDataset(orgSlug, dsSlug);

    public Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug) => _queries.GetEntry(orgSlug, dsSlug);

    public Task<StampDto> GetStamp(string orgSlug, string dsSlug) => _queries.GetStamp(orgSlug, dsSlug);

    public Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset) => _commands.AddNew(orgSlug, dataset);

    public Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset) => _commands.Edit(orgSlug, dsSlug, dataset);

    public Task Delete(string orgSlug, string dsSlug) => _commands.Delete(orgSlug, dsSlug);

    public Task Rename(string orgSlug, string dsSlug, string newSlug) => _commands.Rename(orgSlug, dsSlug, newSlug);

    public Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, AttributesDto attributes) =>
        _commands.ChangeAttributes(orgSlug, dsSlug, attributes);

    public Task<IEnumerable<MoveDatasetResultDto>> MoveToOrganization(string sourceOrgSlug, string[] datasetSlugs,
        string destOrgSlug, ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict) =>
        _mover.MoveToOrganization(sourceOrgSlug, datasetSlugs, destOrgSlug, conflictResolution);
}
