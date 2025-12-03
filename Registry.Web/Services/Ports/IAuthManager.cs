using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;

namespace Registry.Web.Services.Ports;

public interface IAuthManager
{
    public Task<User> GetCurrentUser();
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

    public Task<bool> RequestAccess<T>(T obj, AccessType access, User user) where T: IRequestAccess;
    public Task<bool> RequestAccess<T>(T obj, AccessType access) where T : IRequestAccess;

    public Task<bool> CanListOrganizations(User user);
    public Task<bool> CanListOrganizations();
    public Task<bool> CanRefreshToken(User user);

    public Task<bool> CanRefreshToken();
    
    public Task<DatasetPermissionsDto> GetDatasetPermissions(Dataset dataset);
}