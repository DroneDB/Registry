using System;
using System.ComponentModel;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Registry.Ports;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class AuthManager : IAuthManager
    {
        private readonly UserManager<User> _usersManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IDdbManager _ddbManager;

        public AuthManager(UserManager<User> usersManager, IHttpContextAccessor httpContextAccessor, IDdbManager ddbManager)
        {
            _usersManager = usersManager;
            _httpContextAccessor = httpContextAccessor;
            _ddbManager = ddbManager;
        }

        public async Task<User> GetCurrentUser()
        {
            var userId = _httpContextAccessor.HttpContext?.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            if (userId == null) return null;

            return await _usersManager.FindByIdAsync(userId);
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

        public async Task<bool> IsOwnerOrAdmin(Organization org)
        {
            var user = await GetCurrentUser();

            return user != null && (await IsUserAdmin() || org.OwnerId == user.Id);
        }

        public async Task<bool> UserExists(string userId)
        {
            var user = await _usersManager.FindByIdAsync(userId);
            
            return user != null;
        }

        public async Task<bool> IsOwnerOrAdmin(Dataset ds)
        {
            var user = await GetCurrentUser();

            return user != null && (await IsUserAdmin() || ds.Organization.OwnerId == user.Id);
        }

        public async Task<bool> IsUserAdmin()
        {
            return await IsUserInRole(ApplicationDbContext.AdminRoleName);
        }

        public async Task<bool> RequestAccess<T>(T obj, AccessType access, User user)
        {
            if (obj == null) 
                throw new ArgumentNullException(nameof(obj));
            
            return typeof(T) switch
            {
                _ when typeof(T) == typeof(Organization) => await RequestAccessToOrganization(obj as Organization, access,
                    user),
                _ when typeof(T) == typeof(Dataset) => await RequestAccessToDataset(obj as Dataset, access, user),
                _ => throw new InvalidEnumArgumentException($"Not supported type {typeof(T)}")
            };
        }
        
        public async Task<bool> RequestAccess<T>(T obj, AccessType access)
        {
            return await RequestAccess(obj, access, await GetCurrentUser());
        }

        private async Task<bool> RequestAccessToDataset(Dataset dataset, AccessType access, User user)
        {
            if (dataset == null) 
                throw new ArgumentNullException(nameof(dataset));

            var org = dataset.Organization;
            
            var ddb = _ddbManager.Get(org.Slug, dataset.InternalRef);

            var meta = ddb.Meta.GetSafe();

            // Anonymous users can only read if the dataset is public
            if (user == null) 
                return access == AccessType.Read && (meta.Visibility == Visibility.Public || meta.Visibility == Visibility.Unlisted);
            
            var isOwnerOrAdmin = org.OwnerId == user.Id || await _usersManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);

            // Admins and owners can do anything
            if (isOwnerOrAdmin) 
                return true;

            // Organization users can work on the dataset
            if (org.Users.Any(u => u.UserId == user.Id)) 
                return true;
            
            // If the dataset is public or unlisted, anyone can read it
            if (meta.Visibility == Visibility.Public || meta.Visibility == Visibility.Unlisted) 
                return access == AccessType.Read;
            
            return false;
        }

        private async Task<bool> RequestAccessToOrganization(Organization org, AccessType access, User user)
        {
            if (org == null) 
                throw new ArgumentNullException(nameof(org));

            // Anonymous users can only read if the org is public
            if (user == null) 
                return access == AccessType.Read && org.IsPublic;
            
            var isOwnerOrAdmin = org.OwnerId == user.Id || await _usersManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);

            // Admins and owners can do anything
            if (isOwnerOrAdmin)
            {
                return org.Slug != MagicStrings.PublicOrganizationSlug || access != AccessType.Delete;
            }

            // Organization users can work on the organization even if it is private, but they cannot remove it
            if (access == AccessType.Read || access == AccessType.Write) 
                return org.Users.Any(u => u.UserId == user.Id);

            return false;

        }
    }

    public enum AccessType
    {
        Read = 0, Write = 1, Delete = 2
    }
    
    
}