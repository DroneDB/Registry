using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models;

namespace Registry.Web.Services.Ports
{
    public interface IAuthManager
    {
        public Task<User> GetCurrentUser();
        public Task<bool> IsUserAdmin();
        public Task<bool> IsUserInRole(string roleName);

        public Task<bool> UserExists(string name);
    }
}
