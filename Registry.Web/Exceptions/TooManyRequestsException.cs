#nullable enable
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
}
