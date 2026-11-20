using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Middlewares;

public class JwtInCookieMiddleware : IMiddleware
{
    private readonly AuthTokenOptions _settings;

    private const string AuthorizationHeaderKey = "Authorization";
        
    public JwtInCookieMiddleware(IOptions<AuthTokenOptions> settings)
    {
        _settings = settings.Value;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // The startup validation refuses an empty AuthCookieName
        var cookie = context.Request.Cookies[_settings.AuthCookieName!];

        if (cookie != null)
        {
            var authValue = "Bearer " + cookie;

            if (!context.Request.Headers.ContainsKey(AuthorizationHeaderKey))
                context.Request.Headers[AuthorizationHeaderKey] = authValue;
        }

        await next.Invoke(context);
    }
}