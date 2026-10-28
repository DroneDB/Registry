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
        // Capture embed flag before response starts
        var isEmbed = context.Request.Query["embed"] == "1";

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";

            // Allow cross-origin embedding for embed routes, otherwise restrict to same-origin
            if (!isEmbed)
                headers.XFrameOptions = "SAMEORIGIN";

            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });

        await next(context);
    }
}
