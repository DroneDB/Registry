using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Authorization interface (current user, roles, access control checks).
/// </summary>
public interface IAuthManager
{
    public Task<User?> GetCurrentUser();

    /// <summary>Id of the current user (null when anonymous), from the token and the cached
    /// authorization snapshot: no database query.</summary>
    public Task<string?> GetCurrentUserId();
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

    /// <summary>Whether <paramref name="user"/> may list organizations (false when null).</summary>
    public Task<bool> CanListOrganizations(User? user);
    public Task<bool> CanRefreshToken(User? user);

    public Task<DatasetPermissionsDto> GetDatasetPermissions(Dataset dataset);

    /// <summary>
    /// Whether a listing of datasets shows <paramref name="dataset"/> to the current user: they can read it and, when
    /// it is unlisted, they own its organization, are a member of it or are an administrator. Reading an
    /// unlisted dataset by its link takes <see cref="RequestAccess{T}"/>, not this.
    /// </summary>
    public Task<bool> CanListDataset(Dataset dataset);
}

/// <summary>Lookups of the current user for operations that need one.</summary>
public static class AuthManagerExtensions
{
    /// <summary>The current user; an anonymous caller (or the token of a deleted user) is unauthorized.</summary>
    public static async Task<User> GetRequiredCurrentUser(this IAuthManager authManager) =>
        await authManager.GetCurrentUser() ?? throw new UnauthorizedException("Unauthorized", noRetry: false);
}
