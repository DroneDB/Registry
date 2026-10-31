using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Identity;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IBuildNotificationLevels"/> from the identity database, read directly: the processing nodes, which send
/// the e-mails as often as the web host, have no <c>UserManager</c>. A host with no identity database sends none.
/// </summary>
public sealed class IdentityBuildNotificationLevels : IBuildNotificationLevels
{
    private readonly ApplicationDbContext? _identity;

    /// <param name="identity">The identity database; null on a host that does not register it.</param>
    public IdentityBuildNotificationLevels(ApplicationDbContext? identity = null)
    {
        _identity = identity;
    }

    /// <inheritdoc />
    public async Task<BuildNotificationLevel> OfAsync(string userId, CancellationToken ct)
    {
        if (_identity is null || string.IsNullOrEmpty(userId))
            return BuildNotificationLevel.None;

        var user = await _identity.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
        return user == null ? BuildNotificationLevel.None : BuildNotificationPreference.Of(user);
    }
}
