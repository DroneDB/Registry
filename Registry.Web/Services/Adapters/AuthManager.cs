using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
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
            var userId = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            if (userId == null) return null;

            return await _usersManager.FindByIdAsync(userId);
        }

        public async Task<bool> IsUserInRole(string roleName)
        {
            var currentUser = await GetCurrentUser();
            return currentUser != null && await _usersManager.IsInRoleAsync(currentUser, roleName);
        }

        public async Task<bool> UserExists(string id)
        {
            var user = await _usersManager.FindByIdAsync(id);
            
            return user != null;
        }

        public async Task<bool> IsUserAdmin()
        {
            return await IsUserInRole(ApplicationDbContext.AdminRoleName);
        }
    }
}