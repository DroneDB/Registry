#nullable enable
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// The request body is larger than this server accepts (HTTP 413). Never retryable with the same
/// payload. Raised by <see cref="Attributes.EnforceContentLengthAttribute"/> from the declared
/// <c>Content-Length</c>; Kestrel's own mid-stream 413 is mapped by the classifier.
/// </summary>
public class PayloadTooLargeException : RegistryException
{
    public PayloadTooLargeException(string message)
        : base(StatusCodes.Status413PayloadTooLarge, message, noRetry: true)
    {
    }
}
