using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Identity;
using Serilog.Context;

namespace Registry.Web.Middlewares;

public class UserEnrichmentMiddleware : IMiddleware
{
    private readonly ApplicationDbContext _dbContext;

    public UserEnrichmentMiddleware(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
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

            var user = await _dbContext.Users.FindAsync(userIdClaim);
            if (user != null)
            {
                LogContext.PushProperty("UserName", user.UserName);
            }
        }

        // Call the next middleware in the pipeline
        await next(context);
    }
}