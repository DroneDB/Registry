using System;
using Microsoft.AspNetCore.Http;
using Registry.Common;

namespace Registry.Web.Exceptions;

/// <summary>The user's storage quota is exhausted (HTTP 507). Not retryable with the same payload.</summary>
internal class QuotaExceededException : RegistryException
{
    private const string DefaultMessage = "Storage quota exceeded";

    public QuotaExceededException() : this(DefaultMessage)
    {
    }

    public QuotaExceededException(string message) : this(message, null)
    {
    }

    public QuotaExceededException(long userStorage, long? maxStorage) : this(
        $"Storage quota exceeded: {CommonUtils.GetBytesReadable(userStorage)} out of {(maxStorage == null ? "UNLIMITED" : CommonUtils.GetBytesReadable(maxStorage.Value))}")
    {
    }

    public QuotaExceededException(string message, Exception? innerException)
        : base(StatusCodes.Status507InsufficientStorage, message, noRetry: true, innerException: innerException)
    {
    }
}
