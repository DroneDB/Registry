using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Ends the session of a JWT issued before the password of its user changed. The token carries the security stamp the
/// user had when it was issued (<see cref="Policies.SecurityStampClaimType"/>); Identity replaces the stamp whenever the
/// password changes, so a token whose stamp is not the user's current one is refused (401) on every request, and on
/// a refresh, which is a request like any other. The token that the password change itself returns carries the new
/// stamp, so the user who changed the password stays signed in. A token without the claim, issued before it existed, is
/// accepted until it expires. The current stamp comes from the cached snapshot
/// (<see cref="IUserAuthSnapshotProvider"/>), which a password change invalidates.
/// </summary>
public sealed class SecurityStampJwtEvents : JwtBearerEvents
{
    /// <summary>Why a token with an old stamp is refused (it is for the log; the client gets a 401).</summary>
    public const string StaleStampMessage = "The security stamp of the user changed since the token was issued";

    private readonly IUserAuthSnapshotProvider _snapshots;

    public SecurityStampJwtEvents(IUserAuthSnapshotProvider snapshots)
    {
        _snapshots = snapshots;
    }

    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var stamp = principal?.FindFirstValue(Policies.SecurityStampClaimType);
        var userId = principal?.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(stamp) || string.IsNullOrEmpty(userId))
            return;

        var snapshot = await _snapshots.GetAsync(userId);
        if (!snapshot.Exists || snapshot.SecurityStamp != stamp)
            context.Fail(StaleStampMessage);
    }
}
