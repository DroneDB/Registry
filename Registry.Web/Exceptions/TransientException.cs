using System;

namespace Registry.Web.Exceptions;

/// <summary>
/// A generic transient failure (safe to retry) that is not otherwise typed. Used by
/// <c>IDatasetIndexQueue</c> to wrap a batch-level <c>DdbBusyException</c> once the native
/// layer's own retry budget and the queue's one extra retry are both exhausted, and by any
/// other caller that needs to signal "retry later" without a more specific exception type.
/// Mapped by <see cref="Registry.Web.Utilities.ApiExceptionFilter"/> to 503 + Retry-After.
/// </summary>
public class TransientException : Exception
{
    /// <summary>Suggested seconds for the client to wait before retrying.</summary>
    public int RetryAfterSeconds { get; }

    public TransientException(string message, int retryAfterSeconds = 2) : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public TransientException(string message, Exception innerException, int retryAfterSeconds = 2)
        : base(message, innerException)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}
