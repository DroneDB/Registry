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
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
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
    private readonly ResourceAccessControl _resourceAccess;
    private readonly OrganizationAccessControl _organizationAccess;
    private readonly DatasetAccessControl _datasetAccess;

    public AuthManager(
        UserManager<User> usersManager,
        IHttpContextAccessor httpContextAccessor,
        IDdbManager ddbManager,
        ILogger<AuthManager> logger)
    {
        _usersManager = usersManager;
        _httpContextAccessor = httpContextAccessor;
        _resourceAccess = new ResourceAccessControl(usersManager, logger);
        _organizationAccess = new OrganizationAccessControl(usersManager, logger);
        _datasetAccess = new DatasetAccessControl(usersManager, logger, ddbManager);
    }

    public async Task<User> GetCurrentUser()
    {
        var userId = _httpContextAccessor.HttpContext?.User.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

        return userId == null ? null : await _usersManager.FindByIdAsync(userId);
    }

    public async Task<string> SafeGetCurrentUserName()
    {
        return (await GetCurrentUser())?.UserName ?? MagicStrings.AnonymousUserName;
    }

    public async Task<bool> IsUserInRole(string roleName)
    {
        var currentUser = await GetCurrentUser();
        return currentUser != null && await _usersManager.IsInRoleAsync(currentUser, roleName);
    }

    public async Task<bool> IsUserAdmin() => await IsUserInRole(ApplicationDbContext.AdminRoleName);

    public async Task<bool> IsOwnerOrAdmin(Organization org)
    {
        var user = await GetCurrentUser();
        return await _resourceAccess.HasElevatedPrivileges(user, org.OwnerId);
    }

    public async Task<bool> IsOwnerOrAdmin(Dataset ds)
    {
        var user = await GetCurrentUser();
        return await _resourceAccess.HasElevatedPrivileges(user, ds.Organization.OwnerId);
    }

    public async Task<bool> UserExists(string userId)
    {
        return await _usersManager.FindByIdAsync(userId) != null;
    }

    public async Task<bool> RequestAccess<T>(T obj, AccessType access) where T : IRequestAccess
    {
        return await RequestAccess(obj, access, await GetCurrentUser());
    }

    public async Task<bool> CanListOrganizations(User user)
    {
        // User can list organizations if it's not null and not deactivated
        return user != null && !await _resourceAccess.IsUserDeactivated(user);
    }

    public async Task<bool> CanListOrganizations()
    {
        return await CanListOrganizations(await GetCurrentUser());
    }

    public async Task<bool> CanRefreshToken(User user)
    {
        return user != null && !await _resourceAccess.IsUserDeactivated(user);
    }

    public async Task<bool> CanRefreshToken()
    {
        return await CanRefreshToken(await GetCurrentUser());
    }

    public async Task<bool> RequestAccess<T>(T obj, AccessType access, User user) where T : IRequestAccess
    {
        ArgumentNullException.ThrowIfNull(obj);

        return typeof(T) switch
        {
            var t when t == typeof(Organization) =>
                await _organizationAccess.CanAccessOrganization(obj as Organization, access, user),
            var t when t == typeof(Dataset) =>
                await _datasetAccess.CanAccessDataset(obj as Dataset, access, user),
            _ => throw new InvalidEnumArgumentException($"Not supported type {typeof(T)}")
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


