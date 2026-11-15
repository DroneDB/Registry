using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
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

    /// <summary>Detail of the body of a request the rate limiter refused.</summary>
    public const string TooManyRequestsMessage = "Too many requests, try again later";

    public static IServiceCollection AddRegistryRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(UploadProbe, UploadProbePartition);
        });

        // A refusal answers like every other error of the API: ProblemDetails, with Retry-After
        services.AddOptions<RateLimiterOptions>().Configure<ApiErrorResponder, IProblemDetailsService>(
            (options, responder, problemDetails) =>
                options.OnRejected = async (context, ct) =>
                {
                    var httpContext = context.HttpContext;
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                        httpContext.Response.Headers.RetryAfter =
                            Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                    var problem = responder.Create(httpContext, StatusCodes.Status429TooManyRequests,
                        TooManyRequestsMessage, noRetry: false);
                    await ApiErrorResponder.WriteAsync(httpContext, problemDetails, problem, ct: ct);
                });

        return services;
    }

    /// <summary>The partition (one fixed window per user) of <see cref="UploadProbe"/>.</summary>
    public static RateLimitPartition<string> UploadProbePartition(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = UploadProbePermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

    // Authenticated requests share one budget per user, anonymous ones one per client address
    // (the forwarded one behind a known proxy, see ClientIpResolver)
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.Name) is { Length: > 0 } userId
            ? "u:" + userId
            : ClientAddressKey(context);

    // The client address (the forwarded one behind a known proxy, see ClientIpResolver)
    private static string ClientAddressKey(HttpContext context) =>
        context.RequestServices.GetRequiredService<ClientIpResolver>().AnonymousKey(context);
}
