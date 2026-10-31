using System.Threading.Tasks;
using Hangfire.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Filters;

/// <summary>The Hangfire dashboard is for administrators: the one admin rule of the HTTP gates.</summary>
public class HangfireAuthorizationFilter : IDashboardAsyncAuthorizationFilter
{
    public Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return AdminAuthorizationHandler.IsAdminAsync(httpContext.User,
            httpContext.RequestServices.GetRequiredService<IUserAuthSnapshotProvider>());
    }
}
