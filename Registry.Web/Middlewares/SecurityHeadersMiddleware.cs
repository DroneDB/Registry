using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Middlewares;

/// <summary>
/// Adds security-related HTTP headers to all responses.
/// </summary>
public class SecurityHeadersMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });

        await next(context);
    }
}
