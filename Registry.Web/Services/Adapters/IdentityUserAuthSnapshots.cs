using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Identity;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IUserAuthSnapshotProvider"/> from the identity database, read directly in one query: for the access checks
/// made outside a request, on hosts that have no <c>UserManager</c> (the processing nodes). Not cached: a job asks about
/// a few users once, and must see a removal or a deactivation of the moment.
/// </summary>
public sealed class IdentityUserAuthSnapshots : IUserAuthSnapshotProvider
{
    private readonly ApplicationDbContext _identity;

    public IdentityUserAuthSnapshots(ApplicationDbContext identity)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    /// <inheritdoc />
    public async Task<UserAuthSnapshot> GetAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        var admins = IdentityRoleQuery.UserIdsIn(_identity, ApplicationDbContext.AdminRoleName);
        var deactivated = IdentityRoleQuery.UserIdsIn(_identity, ApplicationDbContext.DeactivatedRoleName);

        var user = await _identity.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.UserName, IsAdmin = admins.Contains(u.Id), IsDeactivated = deactivated.Contains(u.Id) })
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return user == null ? UserAuthSnapshot.Missing : new UserAuthSnapshot(true, user.UserName, user.IsAdmin, user.IsDeactivated);
    }
}
