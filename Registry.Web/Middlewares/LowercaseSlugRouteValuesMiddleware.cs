using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Registry.Web.Utilities;

namespace Registry.Web.Middlewares;

/// <summary>
/// Lowercases the organization and dataset slugs of the route once an endpoint is selected, before the authorization,
/// the MVC filters and the model binding read them. The slug route constraint
/// (<see cref="SlugRouteConstraint"/>) accepts a slug in any letter case, and slugs are stored in lowercase, so a URL typed
/// by hand with a capital letter (<c>/orgs/MyOrg/ds/Photos</c>) reaches the same organization and dataset as the
/// lowercase one. Runs after <c>UseRouting</c>; the request path is left as it was sent.
/// </summary>
public sealed class LowercaseSlugRouteValuesMiddleware : IMiddleware
{
    private static readonly string[] SlugKeys = [RoutesHelper.OrganizationSlugKey, RoutesHelper.DatasetSlugKey];

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var values = context.Request.RouteValues;
        foreach (var key in SlugKeys)
        {
            if (values.TryGetValue(key, out var value) && value is string slug)
                values[key] = slug.ToLowerInvariant();
        }

        return next(context);
    }
}
