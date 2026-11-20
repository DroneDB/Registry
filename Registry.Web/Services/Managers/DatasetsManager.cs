using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The datasets API (<see cref="IDatasetsManager"/>, what the controllers and the organizations call) over the
/// dataset services it is split into: queries, commands, the move between organizations.
/// </summary>
public class DatasetsManager : IDatasetsManager
{
    private readonly DatasetQueries _queries;
    private readonly DatasetCommands _commands;
    private readonly DatasetMover _mover;

    public DatasetsManager(DatasetQueries queries, DatasetCommands commands, DatasetMover mover)
    {
        _queries = queries;
        _commands = commands;
        _mover = mover;
    }

    public Task<IEnumerable<DatasetDto>> List(string orgSlug, bool includeThumbnailCheck = false, CancellationToken ct = default) =>
        _queries.List(orgSlug, includeThumbnailCheck, ct: ct);

    public Task<(IReadOnlyList<DatasetDto> Items, int Total)> ListPage(string orgSlug, DatasetListQuery query,
        bool includeThumbnailCheck = false, CancellationToken ct = default) => _queries.ListPage(orgSlug, query, includeThumbnailCheck, ct: ct);

    public Task<DatasetDto> Get(string orgSlug, string dsSlug, CancellationToken ct = default) => _queries.GetDataset(orgSlug, dsSlug, ct: ct);

    public Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug, CancellationToken ct = default) => _queries.GetEntry(orgSlug, dsSlug, ct: ct);

    public Task<StampDto> GetStamp(string orgSlug, string dsSlug, CancellationToken ct = default) => _queries.GetStamp(orgSlug, dsSlug, ct: ct);

    public Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset, CancellationToken ct = default) => _commands.AddNew(orgSlug, dataset, ct: ct);

    public Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset, CancellationToken ct = default) => _commands.Edit(orgSlug, dsSlug, dataset, ct: ct);

    public Task Delete(string orgSlug, string dsSlug, CancellationToken ct = default) => _commands.Delete(orgSlug, dsSlug, ct: ct);

    public Task Rename(string orgSlug, string dsSlug, string newSlug, CancellationToken ct = default) => _commands.Rename(orgSlug, dsSlug, newSlug, ct: ct);

    [Obsolete("Use meta")]
    public Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, AttributesDto? attributes, CancellationToken ct = default) =>
        _commands.ChangeAttributes(orgSlug, dsSlug, attributes, ct: ct);

    public Task<IEnumerable<MoveDatasetResultDto>> MoveToOrganization(string? sourceOrgSlug, string[] datasetSlugs,
        string destOrgSlug, ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict, CancellationToken ct = default) =>
        _mover.MoveToOrganization(sourceOrgSlug, datasetSlugs, destOrgSlug, conflictResolution, ct: ct);
}
