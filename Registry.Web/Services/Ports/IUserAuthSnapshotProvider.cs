#nullable enable
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Identity facts that authorization checks need about one user.
/// </summary>
/// <param name="Exists">The user exists in the identity store.</param>
/// <param name="UserName">User name (null when the user does not exist).</param>
/// <param name="IsAdmin">The user has the admin role.</param>
/// <param name="IsDeactivated">The user has the deactivated role.</param>
public sealed record UserAuthSnapshot(bool Exists, string? UserName, bool IsAdmin, bool IsDeactivated)
{
    public static readonly UserAuthSnapshot Missing = new(false, null, false, false);

    /// <summary>The user exists and is not deactivated.</summary>
    public bool IsActive => Exists && !IsDeactivated;
}

/// <summary>
/// Source of <see cref="UserAuthSnapshot"/>s for authorization. Snapshots are cached across
/// requests (AppSettings.AuthorizationCacheExpiration) and invalidated when the identity
/// store changes the user's roles or deletes the user.
/// </summary>
public interface IUserAuthSnapshotProvider
{
    Task<UserAuthSnapshot> GetAsync(string userId);
}
