using System.Threading.Tasks;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>The storage use and limit of users (split from the former utilities service).</summary>
public interface IStorageQuota
{
    /// <summary>The storage <paramref name="user"/> uses (the datasets of the organizations it owns) and its limit.</summary>
    UserStorageInfo GetUserStorage(User user);

    /// <summary>
    /// The storage use and limit of the current user, or null when no limit applies (storage
    /// limiter off, administrator, or no limit set).
    /// </summary>
    Task<UserStorageInfo> GetCurrentUserStorageLimit();

    /// <summary>
    /// Throws <c>QuotaExceededException</c> when <paramref name="size"/> more bytes would exceed
    /// the current user's limit (see <see cref="GetCurrentUserStorageLimit"/>).
    /// </summary>
    Task CheckCurrentUserStorage(long size = 0);
}
