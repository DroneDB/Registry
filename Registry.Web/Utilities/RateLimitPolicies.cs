using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Utilities;

/// <summary>
/// Named rate-limit policies (ASP.NET Core rate limiting middleware).
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// The upload self-test (<c>POST /sys/upload-probe</c>): a few runs per minute per user. One run
    /// is 1 + 6 requests of 8 MB.
    /// </summary>
    public const string UploadProbe = "upload-probe";

    public const int UploadProbePermitsPerMinute = 28;

    /// <summary>
    /// The sign-in (<c>POST /users/authenticate</c>): a few attempts per minute per client address, so passwords cannot
    /// be tried there without limit, whatever account they are tried on (the lockout of each account counts its own).
    /// </summary>
    public const string Login = "login";

    public const int LoginPermitsPerMinute = 10;

    public static IServiceCollection AddRegistryRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return default;
            };

            options.AddPolicy(UploadProbe, UploadProbePartition);
            options.AddPolicy(Login, LoginPartition);
        });

    /// <summary>The partition (one fixed window per user) of <see cref="UploadProbe"/>.</summary>
    public static RateLimitPartition<string> UploadProbePartition(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = UploadProbePermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

    /// <summary>The partition (one fixed window per client address, signed in or not) of <see cref="Login"/>.</summary>
    public static RateLimitPartition<string> LoginPartition(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(ClientAddressKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = LoginPermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

    // Authenticated requests share one budget per user, anonymous ones one per client address
    // (the forwarded one behind a known proxy, see ClientIpResolver)
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? "u:" + userId
            : ClientAddressKey(context);

    // The client address (the forwarded one behind a known proxy, see ClientIpResolver)
    private static string ClientAddressKey(HttpContext context) =>
        context.RequestServices.GetRequiredService<ClientIpResolver>().AnonymousKey(context);
}
