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
using Registry.Web.Identity;
using Registry.Web.Identity.Models;

namespace Registry.Web.Filters
{
    public class BasicAuthFilter : ActionFilterAttribute
    {
        private readonly IUsersManager _usersManager;
        private readonly UserManager<User> _userManager;
        private readonly AppSettings _settings;

        public BasicAuthFilter(IUsersManager usersManager, UserManager<User> userManager, IOptions<AppSettings> settings)
        {
            _usersManager = usersManager;
            _userManager = userManager;
            _settings = settings.Value;
        }

        public static void SendBasicAuthRequest(HttpResponse response)
        {
            response.Headers.Add("WWW-Authenticate", "Basic realm=\"DroneDB\"");
            response.StatusCode = 401;
        }

        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var httpContext = context.HttpContext;

            string authHeader = httpContext.Request.Headers["Authorization"];

            try
            {
                if (authHeader != null && authHeader.StartsWith("Basic"))
                {
                    var encodedUsernamePassword = authHeader["Basic ".Length..].Trim();
                    var encoding = Encoding.GetEncoding("iso-8859-1");
                    var usernamePassword = encoding.GetString(Convert.FromBase64String(encodedUsernamePassword));

                    var seperatorIndex = usernamePassword.IndexOf(':');

                    var username = usernamePassword[..seperatorIndex];
                    var password = usernamePassword[(seperatorIndex + 1)..];
                    
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
