using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Web.CodeGenerators.Mvc.Templates.General;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Utilities.Auth;

public static class AuthorizationExtensions
{
    public static IEndpointConventionBuilder RequireAdminOrMonitorToken(
        this IEndpointConventionBuilder builder)
    {
        return builder.RequireAuthorization(policy =>
        {
            policy.RequireAssertion(context =>
            {
                if (context.Resource is not HttpContext httpContext)
                {
                    if (!context.User.Identity?.IsAuthenticated ?? false)
                        return false;

                    return context.User.HasClaim(ApplicationDbContext.AdminRoleName, "true");
                }

                // Resolve the IOptions<AppSettings> from the service provider
                var appSettings = httpContext.RequestServices.GetRequiredService<IOptions<AppSettings>>();

                // Check if the request contains the Authorization header
                var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
                string token = null;

                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
                {
                    token = authHeader["Bearer ".Length..].Trim();
                }
                else
                {
                    // Fallback to check the "token" query string parameter
                    token = httpContext.Request.Query["token"].FirstOrDefault();
                }

                // Check if the token matches the MonitorToken from AppSettings
                if (!string.IsNullOrEmpty(token) && token == appSettings.Value.MonitorToken)
                {
                    // Token is valid, bypass standard authorization
                    return true;
                }

                // Check if the user is authenticated
                if (!context.User.Identity?.IsAuthenticated ?? false)
                    return false;

                // Check if the user is admin
                return context.User.HasClaim(ApplicationDbContext.AdminRoleName, "true");

            });
        });
    }
}