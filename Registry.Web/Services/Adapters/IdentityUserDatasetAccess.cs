#nullable enable
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
/// <see cref="IUserDatasetAccess"/> through the access rules of the API (<see cref="DatasetAccessControl"/>, which
/// <see cref="AuthManager"/> applies to the caller of a request), with the user's roles read straight from the identity
/// database (<see cref="IdentityUserAuthSnapshots"/>), so every host that runs jobs can ask. A host with no identity
/// database can tell nothing and answers no.
/// </summary>
public sealed class IdentityUserDatasetAccess : IUserDatasetAccess
{
    private readonly RegistryContext _registry;
    private readonly DatasetAccessControl? _access;

    /// <param name="registry">The Registry database (datasets, organizations, members).</param>
    /// <param name="ddbManager">The indexes, for the visibility of a dataset not in the cache.</param>
    /// <param name="cache">The cache of the visibilities.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="identity">The identity database; null on a host that does not register it.</param>
    public IdentityUserDatasetAccess(RegistryContext registry, IDdbManager ddbManager, ICacheManager cache,
        ILogger<IdentityUserDatasetAccess> logger, ApplicationDbContext? identity = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _access = identity == null
            ? null
            : new DatasetAccessControl(new IdentityUserAuthSnapshots(identity), registry, logger, ddbManager, cache);
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
}
