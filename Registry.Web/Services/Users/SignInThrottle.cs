using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Users;

/// <summary>
/// The one limit on password guessing by client address: a few sign-in attempts per minute from each address
/// (the forwarded one behind a known proxy, see <see cref="ClientIpResolver"/>), whatever accounts they try (the lockout
/// of each account counts its own). Every way of presenting a password goes through it, so none is a way around it:
/// <c>POST /users/authenticate</c> counts every attempt (<see cref="CountAttempt"/>); HTTP Basic, which OGC clients send
/// with every request, asks first (<see cref="EnsureAllowed"/>) and counts only the credentials that fail, so a client
/// with the right password is never limited.
/// </summary>
public sealed class SignInThrottle : IDisposable
{
    public const int PermitsPerMinute = 10;

    private const string TooManyAttemptsMessage = "Too many sign-in attempts, try again later";

    private readonly ClientIpResolver _clientIps;
    private readonly PartitionedRateLimiter<string> _limiter;

    public SignInThrottle(ClientIpResolver clientIps)
    {
        _clientIps = clientIps;
        _limiter = PartitionedRateLimiter.Create<string, string>(address =>
            RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    }

    /// <summary>Refuses the request when its address has used up its attempts of this minute; consumes nothing.</summary>
    /// <exception cref="TooManyRequestsException">The attempts of this minute are spent.</exception>
    public void EnsureAllowed(HttpContext context) => Acquire(context, 0);

    /// <summary>Counts one attempt of the address of the request.</summary>
    /// <exception cref="TooManyRequestsException">The attempts of this minute are spent.</exception>
    public void CountAttempt(HttpContext context) => Acquire(context, 1);

    private void Acquire(HttpContext context, int permits)
    {
        using var lease = _limiter.AttemptAcquire(_clientIps.AnonymousKey(context), permits);
        if (lease.IsAcquired)
            return;

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
            : 60;
        throw new TooManyRequestsException(TooManyAttemptsMessage, retryAfter);
    }

    public void Dispose() => _limiter.Dispose();
}
