#nullable enable
using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Registry.Web.Identity;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Authorization policy names and the claim rules behind them. Every admin claim check on the request
/// principal goes through <see cref="IsAdmin"/>, so all authentication paths agree on what the token says;
/// the gates combine it with the identity store (<see cref="AdminAuthorizationHandler"/>).
/// </summary>
public static class Policies
{
    /// <summary>
    /// Requires an administrator (<see cref="AdminRequirement"/>: the admin claim and the admin role of an
    /// active user). Every admin-only endpoint declares it; a refusal is a ProblemDetails 403
    /// (<see cref="RegistryAuthorizationResultHandler"/>).
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>Type of the admin flag claim issued by the JWT and by <c>BasicAuthFilter</c>.</summary>
    public static readonly string AdminClaimType = ApplicationDbContext.AdminRoleName.ToLowerInvariant();

    /// <summary>
    /// True when <paramref name="user"/> is authenticated and its admin claim is "true" in any case:
    /// the JWT carries "true", <c>BasicAuthFilter</c> issues <see cref="bool.ToString()"/> ("True").
    /// </summary>
    public static bool IsAdmin(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true &&
        user.Claims.Any(c => string.Equals(c.Type, AdminClaimType, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(c.Value, bool.TrueString, StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers the Registry authorization policies.</summary>
    public static AuthorizationOptions AddRegistryPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(Admin, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new AdminRequirement()));
        return options;
    }
}
