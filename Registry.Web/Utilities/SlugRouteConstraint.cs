using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Registry.Web.Utilities;

/// <summary>
/// The <c>slug</c> route constraint of <c>{orgSlug:slug}</c> and <c>{dsSlug:slug}</c>: the segment is
/// <see cref="SlugRules.IsWellFormed"/> in any letter case, for matching a request and for generating a link
/// (<c>CreatedAtRoute</c>), so a slug that cannot be routed cannot be created either. A slug is stored in lowercase, and
/// a request that spells it with capitals (a URL typed by hand) is routed and then lowercased before anything reads it
/// (<see cref="Middlewares.LowercaseSlugRouteValuesMiddleware"/>).
/// </summary>
public sealed class SlugRouteConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values,
        RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out var value) &&
        SlugRules.IsWellFormed(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.ToLowerInvariant());
}
