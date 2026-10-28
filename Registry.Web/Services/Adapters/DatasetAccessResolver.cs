#nullable enable
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Scoped <see cref="IDatasetAccessResolver"/>: lookups through <see cref="IDatasetResolver"/>, decisions through
/// <see cref="IAuthManager"/>, indexes through <see cref="IDdbManager"/>. The only place that turns a
/// refused access check into an exception.
/// </summary>
public sealed class DatasetAccessResolver : IDatasetAccessResolver
{
    private readonly IDatasetResolver _datasets;
    private readonly IAuthManager _authManager;
    private readonly IDdbManager _ddbManager;
    private readonly DatasetPathPolicy _paths;

    // Datasets granted in this scope, by (organization, dataset, access): authorize once per request
    private readonly ConcurrentDictionary<(string OrgSlug, string DsSlug, AccessType Access), Dataset> _grants = new();

    public DatasetAccessResolver(IDatasetResolver datasets, IAuthManager authManager, IDdbManager ddbManager,
        DatasetPathPolicy paths)
    {
        _datasets = datasets;
        _authManager = authManager;
        _ddbManager = ddbManager;
        _paths = paths;
    }

    public async Task<DatasetAccess> ResolveAsync(string orgSlug, string dsSlug, AccessType access,
        string? path = null, CancellationToken ct = default)
    {
        var dataset = await AuthorizeAsync(orgSlug, dsSlug, access, ct);

        if (path == null)
            return new DatasetAccess(dataset, () => _ddbManager.Get(orgSlug, dataset.InternalRef));

        var normalized = _paths.NormalizeRequestPath(path);
        var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);
        return new DatasetAccess(dataset, () => ddb, normalized, ddb.GetEntry(normalized));
    }

    public async Task EnsureAccessAsync(Dataset dataset, AccessType access)
    {
        if (!await _authManager.RequestAccess(dataset, access))
            throw new ForbiddenException(AccessDeniedMessages.Dataset(access));
    }

    public async Task<Organization> ResolveOrganizationAsync(string orgSlug, AccessType access,
        bool withTracking = false, bool withDatasets = false, CancellationToken ct = default)
    {
        // Without safe the lookup throws when the organization is missing: never null
        var organization = (await _datasets.GetOrganizationAsync(orgSlug, withTracking: withTracking,
            withDatasets: withDatasets, ct: ct))!;
        await EnsureAccessAsync(organization, access);
        return organization;
    }

    public async Task EnsureAccessAsync(Organization organization, AccessType access)
    {
        if (!await _authManager.RequestAccess(organization, access))
            throw new ForbiddenException(AccessDeniedMessages.Organization(access));
    }

    private async Task<Dataset> AuthorizeAsync(string orgSlug, string dsSlug, AccessType access,
        CancellationToken ct)
    {
        var key = (orgSlug, dsSlug, access);
        if (_grants.TryGetValue(key, out var granted))
            return granted;

        // Without safe the lookup throws when the dataset is missing: never null
        var dataset = (await _datasets.GetDatasetAsync(orgSlug, dsSlug, ct: ct))!;
        await EnsureAccessAsync(dataset, access);
        _grants[key] = dataset;
        return dataset;
    }
}
