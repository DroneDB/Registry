using System;
using Microsoft.AspNetCore.Http;
using Registry.Common;
using Registry.Web.Utilities;

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

    /// <summary>
    /// The owner of the organization has no storage left for the write (FR-XC-26, D-039). The text is the one 507 text
    /// of the quota gate, on submit and in the worker, with the size written as the Hub writes it.
    /// </summary>
    /// <param name="remainingBytes">What the owner has left; below 0 reads as 0.</param>
    public static QuotaExceededException OwnerOverQuota(long remainingBytes) => new(OwnerOverQuotaMessage(remainingBytes));

    /// <summary>The 507 text of the storage quota gate (see <see cref="OwnerOverQuota"/>).</summary>
    public static string OwnerOverQuotaMessage(long remainingBytes) =>
        $"Storage quota exceeded: the owner of this organization has {ByteFormat.Format(Math.Max(0, remainingBytes))} left.";

    public QuotaExceededException(string message, Exception? innerException)
        : base(StatusCodes.Status507InsufficientStorage, message, noRetry: true, innerException: innerException)
    {
    }
}
