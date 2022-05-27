using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;

namespace Registry.Web.Services.Ports
{
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

    }
}
