using System.Collections.Generic;
using Registry.Web.Models;

namespace Registry.Web.Services
{
    public interface IUserService
    {
        AuthenticateResponse Authenticate(AuthenticateRequest model);
        IEnumerable<User> GetAll();
    }
}