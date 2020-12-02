using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services
{
    public class JwtInCookieMiddleware : IMiddleware
    {
        private readonly AppSettings _settings;

        public JwtInCookieMiddleware(IOptions<AppSettings> settings)
        {
            _settings = settings.Value;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var cookie = context.Request.Cookies[_settings.AuthCookieName];

            if (cookie != null)
                if (!context.Request.Headers.ContainsKey("Authorization"))
                    context.Request.Headers.Append("Authorization", "Bearer " + cookie);

            await next.Invoke(context);
        }
    }
}