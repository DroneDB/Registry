using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Registry.Web.Models
{
    public class ControllerBaseEx : ControllerBase
    {

        protected readonly UserManager<User> UsersManager;

        public ControllerBaseEx(UserManager<User> usersManager)
        {
            UsersManager = usersManager;
        }

        protected async Task<User> GetCurrentUser()
        {
            var userId = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            if (userId == null) return null;

            return await UsersManager.FindByIdAsync(userId);
        }

        protected async Task<bool> IsUserInRole(string roleName)
        {
            var currentUser = await GetCurrentUser();
            return currentUser != null && await UsersManager.IsInRoleAsync(currentUser, roleName);
        }

        protected async Task<bool> IsUserAdmin()
        {
            return await IsUserInRole(ApplicationDbContext.AdminRoleName);
        }
    }
}
