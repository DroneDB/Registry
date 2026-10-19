using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Registry.Web.Services.Ports;
using Serilog.Context;

namespace Registry.Web.Middlewares;

public class UserEnrichmentMiddleware : IMiddleware
{
    private readonly IUserAuthSnapshotProvider _snapshots;

    public UserEnrichmentMiddleware(IUserAuthSnapshotProvider snapshots)
    {
        _snapshots = snapshots;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Get user ID from the claims
        var userIdClaim = context.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

        if (userIdClaim != null)
        {
            // Enrich Serilog with the user ID
            LogContext.PushProperty("UserId", userIdClaim);

            // Get admin claim
            var isAdmin = context.User.Claims.Any(c => c.Type == "admin" && c.Value == "true");

            if (isAdmin)
            {
                LogContext.PushProperty("IsAdmin", true);
            }

            // Same snapshot the access checks of this request use: no query when it is cached
            var user = await _snapshots.GetAsync(userIdClaim);
            if (user.Exists)
            {
                LogContext.PushProperty("UserName", user.UserName);
            }
        }

        // Call the next middleware in the pipeline
        await next(context);
    }
}
