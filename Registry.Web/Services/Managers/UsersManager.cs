#nullable disable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.Managers;

/// <summary>
/// User CRUD interface (authenticate, list, create, delete, roles, password, storage).
/// </summary>
public class UsersManager
{
    private readonly IUserSessions _sessions;
    private readonly UserAccounts _accounts;
    private readonly UserPasswords _passwords;
    private readonly UserRoles _roles;
    private readonly UserDirectory _directory;
    private readonly UserDeletion _deletion;
    private readonly UserPreferences _preferences;

    public UsersManager(IUserSessions sessions, UserAccounts accounts, UserPasswords passwords, UserRoles roles,
        UserDirectory directory, UserDeletion deletion, UserPreferences preferences)
    {
        _sessions = sessions;
        _accounts = accounts;
        _passwords = passwords;
        _roles = roles;
        _directory = directory;
        _deletion = deletion;
        _preferences = preferences;
    }

    public Task<AuthenticateResponse> Authenticate(string userName, string password) =>
        _sessions.Authenticate(userName, password);

    public Task<AuthenticateResponse> Authenticate(string token) => _sessions.Authenticate(token);

    public Task<AuthenticateResponse> Refresh() => _sessions.Refresh();

    public Task<UserDto> CreateUser(string userName, string email, string password, string[] roles) =>
        _accounts.CreateUser(userName, email, password, roles);

    public Task UpdateUser(string userName, string email) => _accounts.UpdateUser(userName, email);

    public Task<Dictionary<string, object>> GetUserMeta(string userName = null) => _accounts.GetUserMeta(userName);

    public Task SetUserMeta(string userId, Dictionary<string, object> meta) => _accounts.SetUserMeta(userId, meta);

    public Task<Dictionary<string, object>> GetPreferences() => _preferences.Get();

    public Task<Dictionary<string, object>> PatchPreferences(Dictionary<string, JsonElement> patch, CancellationToken ct) =>
        _preferences.Patch(patch, ct);

    public Task<ChangePasswordResult> ChangePassword(string userName, string currentPassword, string newPassword) =>
        _passwords.ChangePassword(userName, currentPassword, newPassword);

    public Task<ChangePasswordResult> ChangePassword(string currentPassword, string newPassword) =>
        _passwords.ChangePassword(currentPassword, newPassword);

    public Task<string[]> GetRoles(CancellationToken ct = default) => _roles.GetRoles(ct: ct);

    public Task CreateRole(string roleName) => _roles.CreateRole(roleName);

    public Task DeleteRole(string roleName) => _roles.DeleteRole(roleName);

    public Task UpdateUserRoles(string userName, string[] roles) => _roles.UpdateUserRoles(userName, roles);

    public Task<IEnumerable<UserDto>> GetAll(CancellationToken ct = default) => _directory.GetAll(ct: ct);

    public Task<IEnumerable<UserDetailDto>> GetAllDetailed(CancellationToken ct = default) => _directory.GetAllDetailed(ct: ct);

    public Task<UserStorageInfo> GetUserStorageInfo(string userName = null) => _directory.GetUserStorageInfo(userName);

    public Task<UserOrganizationMembershipDto[]> GetUserOrganizations(string userName, CancellationToken ct = default) =>
        _directory.GetUserOrganizations(userName, ct: ct);

    public Task AddUserToOrganization(string userName, string orgSlug, OrganizationPermissions permissions) =>
        _directory.AddUserToOrganization(userName, orgSlug, permissions);

    public Task RemoveUserFromOrganization(string userName, string orgSlug) =>
        _directory.RemoveUserFromOrganization(userName, orgSlug);

    public Task UpdateUserOrganizationPermissions(string userName, string orgSlug,
        OrganizationPermissions permissions) =>
        _directory.UpdateUserOrganizationPermissions(userName, orgSlug, permissions);

    /// <summary>
    /// Deletes a user and optionally transfers their data to a successor.
    /// </summary>
    /// <param name="userName">The username of the user to delete.</param>
    /// <param name="successor">The username of the successor to transfer data to. If null, all data is deleted.</param>
    /// <param name="conflictResolution">How to handle conflicts when transferring datasets.</param>
    /// <returns>Result of the deletion operation.</returns>
    public Task<DeleteUserResultDto> DeleteUser(string userName, string successor = null,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.Rename) =>
        _deletion.DeleteUser(userName, successor, conflictResolution);
}
