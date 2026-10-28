#nullable disable
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Registry.Web.Utilities;

/// <summary>
/// The anonymous liveness probe (<see cref="MagicStrings.LivenessUrl"/>) shared by every HTTP host
/// (web server and thumbnail generator), so the container HEALTHCHECK works for both.
/// It answers <c>200 ok</c> as long as the process serves HTTP: no database, cache or native ddb
/// checks and no details (those stay on the token-guarded <see cref="MagicStrings.QuickHealthUrl"/>
/// and <see cref="MagicStrings.HealthUrl"/>).
/// </summary>
public static class LivenessEndpoint
{
    /// <summary>The fixed response body.</summary>
    public const string Body = "ok";

    /// <summary>Maps <c>GET</c> <see cref="MagicStrings.LivenessUrl"/> (anonymous, not cached, not in the API docs).</summary>
    public static IEndpointConventionBuilder MapLiveness(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(MagicStrings.LivenessUrl, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Text(Body, "text/plain");
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
}
