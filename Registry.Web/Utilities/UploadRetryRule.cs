using System;
using Microsoft.AspNetCore.Http;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities;

/// <summary>
/// Whether an upload answer can be retried (FR-XC-12, FR-UPL-07): a 401, 403, 404, 409, 413 or 507 answer to an upload
/// carries <c>noRetry: true</c>, as sending the same file again meets the same refusal. A
/// <see cref="RecoverableConflictException"/> stays retryable: the chunks that are missing or the upload still in
/// progress let the same request succeed later. The rule applies to the upload endpoints only
/// (<see cref="UploadAnswerFilter"/>, the per-file results): elsewhere a 404 can be a build that is not done yet, so
/// <see cref="ApiExceptionClassifier"/> keeps each exception's own flag.
/// </summary>
public static class UploadRetryRule
{
    /// <summary>Whether an upload answered with <paramref name="statusCode"/> fails again when it is sent again.</summary>
    /// <returns>True for 401, 403, 404, 409, 413 and 507.</returns>
    public static bool IsFinalStatus(int statusCode) => statusCode is StatusCodes.Status401Unauthorized
        or StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound or StatusCodes.Status409Conflict
        or StatusCodes.Status413PayloadTooLarge or StatusCodes.Status507InsufficientStorage;

    /// <summary>The <c>noRetry</c> flag of an upload answer that <paramref name="error"/> ended.</summary>
    /// <returns>The classifier's flag, or true for a final status unless the error is recoverable.</returns>
    public static bool NoRetry(ApiErrorDescriptor descriptor, Exception error) =>
        descriptor.NoRetry || (IsFinalStatus(descriptor.StatusCode) && error is not RecoverableConflictException);
}
