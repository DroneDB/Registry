using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// The requirement of the <see cref="Policies.Admin"/> policy, checked by
/// <see cref="AdminAuthorizationHandler"/>.
/// </summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// The monitoring endpoints' requirement (<see cref="AuthorizationExtensions.RequireAdminOrMonitorToken"/>):
/// the configured MonitorToken, or an administrator. Checked by <see cref="AdminAuthorizationHandler"/>.
/// </summary>
public sealed class AdminOrMonitorTokenRequirement : IAuthorizationRequirement;

/// <summary>
/// The one admin rule of every HTTP gate (the <see cref="Policies.Admin"/> policy, the admin arm of
/// <see cref="AdminOrMonitorTokenRequirement"/>, the Hangfire dashboard): the caller
/// is authenticated, <b>its token says admin</b> (<see cref="Policies.IsAdmin"/>, the claim the Hub also
/// reads) <b>and the identity store still says admin</b> for an active user (the cached auth snapshot that
/// <see cref="IAuthManager.IsUserAdmin"/> reads). It is the conjunction of the two rules the gates used
/// separately, so no gate became more permissive: a user promoted after signing in is an admin from the
/// next token (when the Hub shows the admin pages too), a demoted, deactivated or deleted administrator
/// loses the gates at once.
/// </summary>
public sealed class AdminAuthorizationHandler : IAuthorizationHandler
{
    private readonly IUserAuthSnapshotProvider _snapshots;
    private readonly AuthTokenOptions _settings;

    public AdminAuthorizationHandler(IUserAuthSnapshotProvider snapshots, IOptions<AuthTokenOptions> settings)
    {
        _snapshots = snapshots;
        _settings = settings.Value;
    }

    /// <summary>Whether <paramref name="user"/> passes the admin rule.</summary>
    public static async Task<bool> IsAdminAsync(ClaimsPrincipal? user, IUserAuthSnapshotProvider snapshots)
    {
        if (!Policies.IsAdmin(user))
            return false;

        var userId = user!.FindFirst(ClaimTypes.Name)?.Value;
        if (userId == null)
            return false;

        var snapshot = await snapshots.GetAsync(userId);
        return snapshot.IsActive && snapshot.IsAdmin;
    }

    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        bool? isAdmin = null;
        async Task<bool> IsAdmin() => isAdmin ??= await IsAdminAsync(context.User, _snapshots);

        foreach (var requirement in context.PendingRequirements.ToList())
        {
            var passes = requirement switch
            {
                AdminRequirement => await IsAdmin(),
                AdminOrMonitorTokenRequirement => (context.Resource is HttpContext httpContext &&
                                                   AuthorizationExtensions.HasMonitorToken(httpContext,
                                                       _settings.MonitorToken)) || await IsAdmin(),
                _ => false
            };
            if (passes)
                context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Answers a signed-in caller refused by the <see cref="Policies.Admin"/> policy with a ProblemDetails 403
/// and one message (<see cref="AccessDeniedMessages.AdminRequired"/>, <c>noRetry: true</c>), like every
/// other refusal of the API. Every other outcome (an anonymous caller's challenge, other policies)
/// is the framework's default.
/// </summary>
public sealed class RegistryAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();
    private readonly ApiErrorResponder _responder;
    private readonly IProblemDetailsService _problemDetails;

    public RegistryAuthorizationResultHandler(ApiErrorResponder responder, IProblemDetailsService problemDetails)
    {
        _responder = responder;
        _problemDetails = problemDetails;
    }

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden ||
            authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<AdminRequirement>().Any() != true)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var problem = _responder.Create(context, StatusCodes.Status403Forbidden, AccessDeniedMessages.AdminRequired,
            noRetry: true);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        if (!await _problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }))
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null,
                ApiErrorResponder.ProblemJsonContentType);
    }
}
