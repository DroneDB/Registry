using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IUsersManager
    {
        Task<AuthenticateResponse> Authenticate(string userName, string password);
        Task<IEnumerable<UserDto>> GetAll();
        Task CreateUser(string userName, string email, string password);
        Task DeleteUser(string userName);
        Task ChangePassword(string userName, string oldPassword, string newPassword);
    }
}
