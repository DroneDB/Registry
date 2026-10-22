using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The users API (<see cref="IUsersManager"/>, what <c>UsersController</c> calls) over the user services RQ-12 split it
/// into (1000 lines and 14 parameters before): sign-in, accounts, passwords, roles, the directory, the deletion.
/// </summary>
public class UsersManager : IUsersManager
{
    private readonly IUserSessions _sessions;
    private readonly IUserAccounts _accounts;
    private readonly IUserPasswords _passwords;
    private readonly IUserRoles _roles;
    private readonly IUserDirectory _directory;
    private readonly IUserDeletion _deletion;

    public UsersManager(IUserSessions sessions, IUserAccounts accounts, IUserPasswords passwords, IUserRoles roles,
        IUserDirectory directory, IUserDeletion deletion)
    {
        _sessions = sessions;
        _accounts = accounts;
        _passwords = passwords;
        _roles = roles;
        _directory = directory;
        _deletion = deletion;
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

    public Task<ChangePasswordResult> ChangePassword(string userName, string currentPassword, string newPassword) =>
        _passwords.ChangePassword(userName, currentPassword, newPassword);

    public Task<ChangePasswordResult> ChangePassword(string currentPassword, string newPassword) =>
        _passwords.ChangePassword(currentPassword, newPassword);

    public Task<string[]> GetRoles() => _roles.GetRoles();

    public Task CreateRole(string roleName) => _roles.CreateRole(roleName);

    public Task DeleteRole(string roleName) => _roles.DeleteRole(roleName);

    public Task UpdateUserRoles(string userName, string[] roles) => _roles.UpdateUserRoles(userName, roles);

    public Task<IEnumerable<UserDto>> GetAll() => _directory.GetAll();

    public Task<IEnumerable<UserDetailDto>> GetAllDetailed() => _directory.GetAllDetailed();

    public Task<UserStorageInfo> GetUserStorageInfo(string userName = null) => _directory.GetUserStorageInfo(userName);

    public Task<UserOrganizationMembershipDto[]> GetUserOrganizations(string userName) =>
        _directory.GetUserOrganizations(userName);

    public Task AddUserToOrganization(string userName, string orgSlug, OrganizationPermissions permissions) =>
        _directory.AddUserToOrganization(userName, orgSlug, permissions);

    public Task RemoveUserFromOrganization(string userName, string orgSlug) =>
        _directory.RemoveUserFromOrganization(userName, orgSlug);

    public Task UpdateUserOrganizationPermissions(string userName, string orgSlug,
        OrganizationPermissions permissions) =>
        _directory.UpdateUserOrganizationPermissions(userName, orgSlug, permissions);

    public Task<DeleteUserResultDto> DeleteUser(string userName, string successor = null,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.Rename) =>
        _deletion.DeleteUser(userName, successor, conflictResolution);
}
