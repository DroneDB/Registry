using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
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
/// The one writer of every refusal of the authorization middleware (FR-XC-29): each Challenged and
/// Forbidden result of an endpoint's policy ([Authorize], the <see cref="Policies.Admin"/> policy, the
/// monitoring endpoints, the Hangfire dashboard) answers a ProblemDetails body from
/// <see cref="ApiErrorResponder.Create"/>, like every other refusal of the API. A challenge runs the
/// schemes' own challenge first, so the 401 keeps its <c>WWW-Authenticate</c> header, then writes
/// <see cref="AccessDeniedMessages.NotSignedIn"/> (<c>noRetry: false</c>: signing in helps). A refused
/// signed-in caller gets a 403 with the message of the failed rule (<see cref="AccessDeniedMessages.AdminRequired"/>
/// for the admin requirements, <c>noRetry: true</c>). Every other outcome is the framework's default.
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
        if (authorizeResult.Challenged)
        {
            // The schemes' challenge sets the status and WWW-Authenticate; the body is ours
            if (policy.AuthenticationSchemes.Count > 0)
                foreach (var scheme in policy.AuthenticationSchemes)
                    await context.ChallengeAsync(scheme);
            else
                await context.ChallengeAsync();

            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                await WriteAsync(context, StatusCodes.Status401Unauthorized, AccessDeniedMessages.NotSignedIn,
                    noRetry: false);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, ForbiddenMessage(authorizeResult), noRetry: true);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>The message of the failed rule: the admin requirements have theirs, any other a generic one.</summary>
    internal static string ForbiddenMessage(PolicyAuthorizationResult authorizeResult) =>
        authorizeResult.AuthorizationFailure?.FailedRequirements
            .Any(r => r is AdminRequirement or AdminOrMonitorTokenRequirement) == true
            ? AccessDeniedMessages.AdminRequired
            : AccessDeniedMessages.OperationRefused;

    private async Task WriteAsync(HttpContext context, int status, string detail, bool noRetry)
    {
        if (context.Response.HasStarted)
            return;

        var problem = _responder.Create(context, status, detail, noRetry);
        context.Response.StatusCode = status;
        if (!await _problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }))
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null,
                ApiErrorResponder.ProblemJsonContentType);
    }
}
