using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.Ports;

namespace Registry.Web.Filters;

public class HangfireAuthorizationFilter : IDashboardAsyncAuthorizationFilter
{
    public Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var authService = httpContext.RequestServices.GetRequiredService<IAuthManager>();

        return authService.IsUserAdmin();
    }
}