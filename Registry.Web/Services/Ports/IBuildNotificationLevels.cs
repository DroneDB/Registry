#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.Ports;

/// <summary>Which build notification e-mails a user wants, on any host that runs jobs.</summary>
public interface IBuildNotificationLevels
{
    /// <summary>The choice of <paramref name="userId"/> (<see cref="BuildNotificationPreference.Of"/>); none for an unknown user.</summary>
    Task<BuildNotificationLevel> OfAsync(string userId, CancellationToken ct);
}
