using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// The SMTP server refused an e-mail, or the Registry could not reach it (HTTP 502: the upstream mail server
/// failed). This type is permanent: a rejected recipient, a refused login, a certificate that does not validate.
/// Sending the same e-mail again cannot succeed until the message or the settings change.
/// <see cref="TransientEmailDeliveryException"/> is the kind a later attempt can fix.
/// </summary>
public class EmailDeliveryException : RegistryException
{
    public EmailDeliveryException(string message, Exception? innerException = null)
        : this(message, true, innerException)
    {
    }

    private protected EmailDeliveryException(string message, bool noRetry, Exception? innerException)
        : base(StatusCodes.Status502BadGateway, message, noRetry, innerException)
    {
    }

    /// <summary>Whether a later attempt can succeed (the server was unreachable or busy).</summary>
    public bool IsTransient => !NoRetry;
}

/// <summary>
/// The SMTP server was unreachable, timed out or answered "try again later" (a 4xx reply). The queued e-mail
/// job retries only on this type.
/// </summary>
public sealed class TransientEmailDeliveryException : EmailDeliveryException
{
    /// <summary>Seconds a client waits before it sends again.</summary>
    public const int RetryAfter = 60;

    public TransientEmailDeliveryException(string message, Exception? innerException = null)
        : base(message, false, innerException)
    {
    }

    /// <inheritdoc />
    public override int? RetryAfterSeconds => RetryAfter;
}
