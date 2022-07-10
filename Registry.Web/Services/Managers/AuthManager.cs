using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class AuthManager : IAuthManager
    {
        private readonly UserManager<User> _usersManager;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AuthManager(UserManager<User> usersManager, IHttpContextAccessor httpContextAccessor)
        {
            _usersManager = usersManager;
            _httpContextAccessor = httpContextAccessor;
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
    }
}