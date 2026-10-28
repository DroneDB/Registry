#nullable disable
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Authorization interface (current user, roles, access control checks).
/// </summary>
public interface IAuthManager
{
    public Task<User> GetCurrentUser();

    /// <summary>Id of the current user (null when anonymous), from the token and the cached
    /// authorization snapshot: no database query.</summary>
    public Task<string> GetCurrentUserId();
    public Task<bool> IsUserAdmin();
    public Task<bool> IsUserInRole(string roleName);

    /// <summary>
    /// Gets the current user name or anonymous user name if not logged in
    /// </summary>
    /// <returns></returns>
    public Task<string> SafeGetCurrentUserName();

    public Task<bool> IsOwnerOrAdmin(Dataset ds);

    public Task<bool> IsOwnerOrAdmin(Organization org);

    public Task<bool> UserExists(string userId);
    public Task<bool> RequestAccess<T>(T obj, AccessType access) where T : IRequestAccess;

    public Task<bool> CanListOrganizations(User user);
    public Task<bool> CanRefreshToken(User user);

    public Task<DatasetPermissionsDto> GetDatasetPermissions(Dataset dataset);
}