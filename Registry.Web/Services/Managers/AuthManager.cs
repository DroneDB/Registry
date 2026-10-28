#nullable disable
using System;
using System.ComponentModel;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Main authorization manager implementing IAuthManager interface
/// </summary>
public class AuthManager : IAuthManager
{
    private readonly UserManager<User> _usersManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUserAuthSnapshotProvider _snapshots;
    private readonly ResourceAccessControl _resourceAccess;
    private readonly OrganizationAccessControl _organizationAccess;
    private readonly DatasetAccessControl _datasetAccess;

    // Request-scoped lazy cache for current user (AuthManager is Scoped)
    private readonly Lazy<Task<User>> _currentUserLazy;

    public AuthManager(
        UserManager<User> usersManager,
        IHttpContextAccessor httpContextAccessor,
        IDdbManager ddbManager,
        RegistryContext context,
        ILogger<AuthManager> logger,
        ICacheManager cacheManager,
        IUserAuthSnapshotProvider snapshots)
    {
        _usersManager = usersManager;
        _httpContextAccessor = httpContextAccessor;
        _snapshots = snapshots;
        _resourceAccess = new ResourceAccessControl(snapshots, context, logger);
        _organizationAccess = new OrganizationAccessControl(snapshots, context, logger);
        _datasetAccess = new DatasetAccessControl(snapshots, context, logger, ddbManager, cacheManager);

        // Initialize lazy - evaluated once per request (AuthManager is Scoped)
        _currentUserLazy = new Lazy<Task<User>>(LoadCurrentUserAsync);
        _currentUserIdLazy = new Lazy<Task<string>>(LoadCurrentUserIdAsync);
    }

    // Request-scoped lazy cache for the id of the current user (null when anonymous)
    private readonly Lazy<Task<string>> _currentUserIdLazy;

    private string CurrentUserIdClaim => _httpContextAccessor.HttpContext?.User.Claims
        .FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

    private async Task<User> LoadCurrentUserAsync()
    {
        var userId = CurrentUserIdClaim;
        return userId == null ? null : await _usersManager.FindByIdAsync(userId);
    }

    // Access checks only need the id: resolved from the token and the cached snapshot, so a
    // request does not load the user. A token of a deleted user is anonymous, as in
    // GetCurrentUser().
    private async Task<string> LoadCurrentUserIdAsync()
    {
        var userId = CurrentUserIdClaim;
        return userId != null && (await _snapshots.GetAsync(userId)).Exists ? userId : null;
    }

    public Task<User> GetCurrentUser() => _currentUserLazy.Value;

    public Task<string> GetCurrentUserId() => _currentUserIdLazy.Value;

    public async Task<string> SafeGetCurrentUserName()
    {
        return (await GetCurrentUser())?.UserName ?? MagicStrings.AnonymousUserName;
    }

    public async Task<bool> IsUserInRole(string roleName)
    {
        var currentUser = await GetCurrentUser();
        return currentUser != null && await _usersManager.IsInRoleAsync(currentUser, roleName);
    }

    public async Task<bool> IsUserAdmin()
    {
        var userId = await _currentUserIdLazy.Value;
        return userId != null && (await _snapshots.GetAsync(userId)).IsAdmin;
    }

    public async Task<bool> IsOwnerOrAdmin(Organization org)
        => await _resourceAccess.HasElevatedPrivileges(await _currentUserIdLazy.Value, org.OwnerId);

    public async Task<bool> IsOwnerOrAdmin(Dataset ds)
        => await _resourceAccess.HasElevatedPrivileges(await _currentUserIdLazy.Value, ds.Organization.OwnerId);

    public async Task<bool> UserExists(string userId)
    {
        return await _usersManager.FindByIdAsync(userId) != null;
    }

    public async Task<bool> RequestAccess<T>(T obj, AccessType access) where T : IRequestAccess
    {
        return await RequestAccessById(obj, access, await _currentUserIdLazy.Value);
    }

    public async Task<bool> CanListOrganizations(User user)
    {
        // User can list organizations if it's not null and not deactivated
        return user != null && !await _resourceAccess.IsUserDeactivated(user.Id);
    }

    public async Task<bool> CanRefreshToken(User user)
    {
        // Read the identity store, not the snapshot: a refresh extends the token's life
        return user != null && !await _usersManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName);
    }

    private async Task<bool> RequestAccessById<T>(T obj, AccessType access, string userId) where T : IRequestAccess
    {
        ArgumentNullException.ThrowIfNull(obj);

        return typeof(T) switch
        {
            var t when t == typeof(Organization) =>
                await _organizationAccess.CanAccessOrganization(obj as Organization, access, userId),
            var t when t == typeof(Dataset) =>
                await _datasetAccess.CanAccessDataset(obj as Dataset, access, userId),
            _ => throw new InvalidEnumArgumentException($"Not supported type {typeof(T)}")
        };
    }

    public async Task<DatasetPermissionsDto> GetDatasetPermissions(Dataset dataset)
    {
        var userId = await _currentUserIdLazy.Value;

        return new DatasetPermissionsDto
        {
            CanRead = await _datasetAccess.CanAccessDataset(dataset, AccessType.Read, userId),
            CanWrite = await _datasetAccess.CanAccessDataset(dataset, AccessType.Write, userId),
            CanDelete = await _datasetAccess.CanAccessDataset(dataset, AccessType.Delete, userId)
        };
    }
}

// Enum for different access levels
public enum AccessType
{
    Read = 0,
    Write = 1,
    Delete = 2
}


