using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// The caller hit a per-user limit (HTTP 429) and may try again after <see cref="RetryAfterSeconds"/>,
/// e.g. the concurrent-download limit of <c>DownloadLimitMiddleware</c>.
/// </summary>
public class TooManyRequestsException : RegistryException
{
    private readonly int _retryAfterSeconds;

    public TooManyRequestsException(string message, int retryAfterSeconds)
        : base(StatusCodes.Status429TooManyRequests, message)
    {
        _retryAfterSeconds = retryAfterSeconds;
    }

    /// <inheritdoc />
    public override int? RetryAfterSeconds => _retryAfterSeconds;

    /// <summary>
    /// The refusal of an account that Identity's lockout locked after too many wrong passwords (at the sign-in or at
    /// the change of one's own password), until <paramref name="lockoutEnd"/>: the same answer wherever it is checked.
    /// </summary>
    public static TooManyRequestsException AccountLockedOut(DateTimeOffset? lockoutEnd) =>
        new("Too many wrong passwords: the account is locked, try again later",
            Math.Max(1, (int)Math.Ceiling(((lockoutEnd ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds)));
}
