#nullable enable
using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// A generic transient failure (safe to retry) that is not otherwise typed: HTTP 503 with
/// <c>Retry-After</c>. Used by <c>IDatasetIndexQueue</c> to wrap a batch-level <c>DdbBusyException</c>
/// once the native layer's own retry budget and the queue's one extra retry are both exhausted, and by
/// any other caller that needs to signal "retry later" without a more specific exception type.
/// </summary>
public class TransientException : RegistryException
{
    private readonly int _retryAfterSeconds;

    public TransientException(string message, int retryAfterSeconds = 2)
        : this(message, null, retryAfterSeconds)
    {
    }

    public TransientException(string message, Exception? innerException, int retryAfterSeconds = 2)
        : base(StatusCodes.Status503ServiceUnavailable, message, innerException: innerException)
    {
        _retryAfterSeconds = retryAfterSeconds;
    }

    /// <inheritdoc />
    public override int? RetryAfterSeconds => _retryAfterSeconds;
}
