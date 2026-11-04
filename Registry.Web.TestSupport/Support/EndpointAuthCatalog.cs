using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Identity.Models;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Test.Support;

/// <summary>
/// Who passes the HTTP authorization gate of an endpoint (the ASP.NET Core authorization
/// middleware and the MVC authorization filters), before the action and its own access checks run.
/// </summary>
public enum EndpointGate
{
    /// <summary>No authorization metadata: the action (or its manager) decides alone.</summary>
    Open,

    /// <summary>Explicit <c>[AllowAnonymous]</c>.</summary>
    Anonymous,

    /// <summary>Any authenticated user passes; anonymous requests are challenged (401).</summary>
    Authenticated,

    /// <summary>Only admins pass: anonymous requests get 401, other users 403.</summary>
    Admin,

    /// <summary>The effective policy rejects every probe principal (anonymous, user and admin).</summary>
    Nobody
}

/// <summary>One HTTP method of one routed endpoint and the authorization in effect on it.</summary>
/// <param name="Method">HTTP method, or <c>ANY</c> when the endpoint does not restrict it.</param>
/// <param name="Template">Route template; parameter constraints other than regex and slug are kept.</param>
/// <param name="Handler"><c>Controller.Action</c> for MVC actions, the display name otherwise.</param>
/// <param name="Controller">Controller name without the suffix; null for non-MVC endpoints.</param>
/// <param name="Declared">The declared metadata: attributes, inline policies, auth filters.</param>
/// <param name="Gate">Who passes the gate, from evaluating the effective policy.</param>
/// <param name="Endpoint">The endpoint itself (route pattern, metadata).</param>
public sealed record EndpointAuthEntry(
    string Method,
    string Template,
    string Handler,
    string? Controller,
    string Declared,
    EndpointGate Gate,
    RouteEndpoint Endpoint)
{
    /// <summary>"METHOD /template", the identity of the entry in the snapshot.</summary>
    public string Key => $"{Method} {Template}";

    /// <summary>Snapshot line: "METHOD /template -> declared [gate] @ handler".</summary>
    public string SnapshotLine => $"{Key} -> {Declared} [{Gate.ToString().ToLowerInvariant()}] @ {Handler}";
}

/// <summary>
/// Reads every routed endpoint of the running app from its own <see cref="EndpointDataSource"/>
/// and classifies its authorization: the declared metadata (<c>[AllowAnonymous]</c>,
/// <c>[Authorize]</c>, policies, policies added with <c>RequireAuthorization</c>, MVC authorization
/// filters such as <c>BasicAuthFilter</c> and <c>OgcAuthorizationFilter</c>) and the gate, found by
/// evaluating the effective policy for an anonymous, a regular and an admin principal. New
/// controllers and actions are picked up without any change here.
/// </summary>
public static class EndpointAuthCatalog
{
    private const string ControllerSuffix = "Controller";
    private const string AnyMethod = "ANY";

    /// <summary>All endpoints, one entry per HTTP method, sorted by template then method.</summary>
    public static async Task<IReadOnlyList<EndpointAuthEntry>> ReadAsync(IServiceProvider services)
    {
        // The composite of every data source registered by UseEndpoints (MVC, health, Hangfire...)
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var entries = new List<EndpointAuthEntry>();

        using var scope = services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        // The admin gate needs the admin claim and an administrator in the identity store: the
        // admin probe is the factory's default administrator
        var adminId = (await scope.ServiceProvider.GetRequiredService<UserManager<User>>()
            .FindByNameAsync(RegistryWebFactory.AdminUserName))?.Id ?? "probe-admin-id";

        foreach (var endpoint in endpoints)
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var template = RenderTemplate(endpoint.RoutePattern);
            var handler = action != null
                ? $"{action.ControllerTypeInfo.Name}.{action.MethodInfo.Name}"
                : endpoint.DisplayName ?? template;
            var controller = action?.ControllerName;
            var declared = Describe(endpoint, action);
            var gate = await EvaluateGateAsync(endpoint, policyProvider, authorization, scope.ServiceProvider, adminId);

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            foreach (var method in methods is { Count: > 0 } ? methods : [AnyMethod])
                entries.Add(new EndpointAuthEntry(method, template, handler, controller, declared, gate, endpoint));
        }

        return entries
            .OrderBy(e => e.Template, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Handler, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every concrete controller type of the Registry.Web assembly, by route name (no suffix).
    /// Independent of routing, so a controller that maps no endpoint is still listed.
    /// </summary>
    public static IReadOnlyList<string> ControllerNames() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                        typeof(ControllerBase).IsAssignableFrom(t) &&
                        t.Name.EndsWith(ControllerSuffix, StringComparison.Ordinal))
            .Select(t => t.Name[..^ControllerSuffix.Length])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The template with regex and slug constraints dropped (the long patterns, and the slug rules), so the
    /// snapshot stays readable; other constraints, optional and catch-all markers are kept.
    /// </summary>
    public static string RenderTemplate(RoutePattern pattern) => RenderPath(pattern, RenderParameter);

    /// <summary>
    /// The path of <paramref name="pattern"/> with each parameter replaced by
    /// <paramref name="parameter"/> (e.g. a concrete route value to build a request URL).
    /// </summary>
    public static string RenderPath(RoutePattern pattern, Func<RoutePatternParameterPart, string> parameter)
    {
        var segments = pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart value => parameter(value),
            _ => part.ToString()
        })));

        return "/" + string.Join('/', segments);
    }

    private static string RenderParameter(RoutePatternParameterPart parameter)
    {
        var constraints = parameter.ParameterPolicies
            .Select(p => p.Content)
            .Where(c => c != null && !c.StartsWith("regex(", StringComparison.Ordinal) && c != RoutesHelper.SlugConstraint)
            .Select(c => ":" + c);

        var prefix = parameter.IsCatchAll ? (parameter.EncodeSlashes ? "*" : "**") : string.Empty;
        var suffix = parameter.IsOptional ? "?" : string.Empty;
        return "{" + prefix + parameter.Name + string.Concat(constraints) + suffix + "}";
    }

    private static string Describe(Endpoint endpoint, ControllerActionDescriptor? action)
    {
        var parts = new List<string>();

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
            parts.Add("AllowAnonymous");

        foreach (var data in endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
        {
            var args = new List<string>();
            if (!string.IsNullOrEmpty(data.Policy)) args.Add($"Policy={data.Policy}");
            if (!string.IsNullOrEmpty(data.Roles)) args.Add($"Roles={data.Roles}");
            if (!string.IsNullOrEmpty(data.AuthenticationSchemes)) args.Add($"Schemes={data.AuthenticationSchemes}");
            parts.Add(args.Count == 0 ? "Authorize" : $"Authorize({string.Join(", ", args)})");
        }

        foreach (var policy in endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
            parts.Add($"Policy(inline: {string.Join(", ", policy.Requirements.Select(r => r.GetType().Name))})");

        if (action != null)
            parts.AddRange(AuthorizationFilterNames(action).Select(name => $"Filter({name})"));

        return parts.Count == 0 ? "none" : string.Join(" + ", parts.Distinct());
    }

    /// <summary>
    /// The MVC filters on the action that gate access: authorization filters (also when declared
    /// through <see cref="ServiceFilterAttribute"/> or <see cref="TypeFilterAttribute"/>).
    /// </summary>
    private static IEnumerable<string> AuthorizationFilterNames(ControllerActionDescriptor action) =>
        action.FilterDescriptors
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Scope)
            .Select(f => f.Filter switch
            {
                ServiceFilterAttribute service => service.ServiceType,
                TypeFilterAttribute type => type.ImplementationType,
                var filter => filter.GetType()
            })
            .Where(IsAccessFilter)
            .Select(t => t.Name);

    private static bool IsAccessFilter(Type type) =>
        typeof(IAuthorizationFilter).IsAssignableFrom(type) ||
        typeof(IAsyncAuthorizationFilter).IsAssignableFrom(type);

    private static async Task<EndpointGate> EvaluateGateAsync(Endpoint endpoint,
        IAuthorizationPolicyProvider policyProvider, IAuthorizationService authorization,
        IServiceProvider requestServices, string adminId)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
            return EndpointGate.Anonymous;

        // Same combination the authorization middleware performs for the endpoint
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

        if (policy == null)
            return EndpointGate.Open;

        // The middleware passes the HttpContext as resource (RequireAdminOrMonitorToken reads it)
        var resource = new DefaultHttpContext { RequestServices = requestServices };

        async Task<bool> Passes(ClaimsPrincipal principal) =>
            (await authorization.AuthorizeAsync(principal, resource, policy)).Succeeded;

        if (await Passes(Principal(null)))
            return EndpointGate.Anonymous;
        if (await Passes(Principal(false)))
            return EndpointGate.Authenticated;
        return await Passes(Principal(true, adminId)) ? EndpointGate.Admin : EndpointGate.Nobody;
    }

    /// <summary>
    /// Anonymous (null), or an authenticated principal with the claims the app's JWT carries.
    /// </summary>
    private static ClaimsPrincipal Principal(bool? isAdmin, string userId = "probe-user-id") =>
        isAdmin == null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, userId),
                new Claim(Policies.AdminClaimType, isAdmin.Value ? "true" : "false")
            ], "probe"));
}
