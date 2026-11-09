using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IUserDatasetAccess"/> through the access rules of the API (<see cref="DatasetAccessControl"/> and
/// <see cref="OrganizationAccessControl"/>, which <see cref="AuthManager"/> applies to the caller of a request), with the user's roles read straight from the identity
/// database (<see cref="IdentityUserAuthSnapshots"/>), so every host that runs jobs can ask. A host with no identity
/// database can tell nothing and answers no.
/// </summary>
public sealed class IdentityUserDatasetAccess : IUserDatasetAccess
{
    private readonly RegistryContext _registry;
    private readonly DatasetAccessControl? _access;
    private readonly OrganizationAccessControl? _organizations;

    /// <param name="registry">The Registry database (datasets, organizations, members).</param>
    /// <param name="ddbManager">The indexes, for the visibility of a dataset not in the cache.</param>
    /// <param name="cache">The cache of the visibilities.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="identity">The identity database; null on a host that does not register it.</param>
    public IdentityUserDatasetAccess(RegistryContext registry, IDdbManager ddbManager, ICacheManager cache,
        ILogger<IdentityUserDatasetAccess> logger, ApplicationDbContext? identity = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (identity == null)
            return;

        var snapshots = new IdentityUserAuthSnapshots(identity);
        _access = new DatasetAccessControl(snapshots, registry, logger, ddbManager, cache);
        _organizations = new OrganizationAccessControl(snapshots, registry, logger);
    }

    /// <inheritdoc />
    public async Task<bool> CanReadAsync(string userId, string orgSlug, string dsSlug, CancellationToken ct)
    {
        if (_access == null || string.IsNullOrEmpty(userId))
            return false;

        // The members come along: the rules read them
        var dataset = await _registry.Datasets.AsNoTracking()
            .Include(d => d.Organization).ThenInclude(o => o.Users)
            .FirstOrDefaultAsync(d => d.Organization.Slug == orgSlug && d.Slug == dsSlug, ct)
            .ConfigureAwait(false);

        return dataset != null && await _access.CanAccessDataset(dataset, AccessType.Read, userId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CanAccessOrganizationAsync(string userId, string orgSlug, AccessType access,
        CancellationToken ct)
    {
        if (_organizations == null || string.IsNullOrEmpty(userId))
            return false;

        // The members come along: the rules read them
        var organization = await _registry.Organizations.AsNoTracking().Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.Slug == orgSlug, ct)
            .ConfigureAwait(false);

        return organization != null &&
               await _organizations.CanAccessOrganization(organization, access, userId).ConfigureAwait(false);
    }
}
