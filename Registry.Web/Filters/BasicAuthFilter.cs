using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services;
using Registry.Web.Services.Ports;
using System;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace Registry.Web.Filters
{
    public class BasicAuthFilter : ActionFilterAttribute
    {
        IAuthManager _authManager;
        IUsersManager _usersManager;
        UserManager<User> _userManager;
        AppSettings _settings;

        public BasicAuthFilter(IAuthManager authManager, IUsersManager usersManager, UserManager<User> userManager, IOptions<AppSettings> settings)
        {
            _authManager = authManager;
            _usersManager = usersManager;
            _userManager = userManager;
            _settings = settings.Value;
        }

        public static void SendBasicAuthRequest(HttpResponse response)
        {
            response.Headers.Add("WWW-Authenticate", "Basic realm=\"DroneDB\"");
            response.StatusCode = 401;
        }

        override public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            HttpContext httpContext = context.HttpContext;

            string authHeader = httpContext.Request.Headers["Authorization"];

            try
            {
                if (authHeader != null && authHeader.StartsWith("Basic"))
                {
                    string encodedUsernamePassword = authHeader.Substring("Basic ".Length).Trim();
                    Encoding encoding = Encoding.GetEncoding("iso-8859-1");
                    string usernamePassword = encoding.GetString(Convert.FromBase64String(encodedUsernamePassword));

                    int seperatorIndex = usernamePassword.IndexOf(':');

                    var username = usernamePassword.Substring(0, seperatorIndex);
                    var password = usernamePassword.Substring(seperatorIndex + 1);
                    
                    var res = await _usersManager.Authenticate(username, password);
                    if (res != null)
                    {
                        var user = await _userManager.FindByNameAsync(res.Username);
                        var isAdmin = await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);

                        httpContext.Response.Cookies.Append(_settings.AuthCookieName, res.Token);

                        var identity = new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.Name, user.Id),
                            new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), isAdmin.ToString()),
                        }, "authenticated");
                        var principal = new ClaimsPrincipal(identity);

                        httpContext.User = principal;
                    }
                }
            }
            catch (FormatException)
            {
                // Invalid auth header chars
                // Do nothing
            }

            await next();
        }
    }
}
