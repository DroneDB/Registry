using System.Collections.Generic;
using DroneDB.Registry.Web.Models;

namespace DroneDB.Registry.Web.Services
{
    public interface IUserService
    {
        AuthenticateResponse Authenticate(AuthenticateRequest model);
        IEnumerable<User> GetAll();
    }
}