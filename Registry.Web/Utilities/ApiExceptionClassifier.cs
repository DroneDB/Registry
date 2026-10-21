#nullable enable
using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities;

/// <summary>
/// Describes the HTTP response a managed exception maps to: status, optional Retry-After hint, the
/// <c>noRetry</c> flag of the ProblemDetails body, the client-facing message, and the level at which
/// the server logs the incident.
/// </summary>
/// <param name="StatusCode">HTTP status code for the response.</param>
/// <param name="RetryAfterSeconds">Seconds to advertise in the Retry-After header (transient errors only).</param>
/// <param name="NoRetry">True when the client should not retry with the same payload.</param>
/// <param name="Message">Client-facing message (generic for 500 to avoid leaking engine details).</param>
/// <param name="Level">Log level for the server-side incident log.</param>
public readonly record struct ApiErrorDescriptor(
    int StatusCode,
    int? RetryAfterSeconds,
    bool NoRetry,
    string Message,
    LogLevel Level);

/// <summary>
/// The one exception → HTTP mapping of the Registry API (RQ-05). Used by <see cref="ApiErrorResponder"/>
/// (MVC filter, <c>IExceptionHandler</c>) and by the OGC filter for its status codes.
/// <list type="bullet">
/// <item>Every <see cref="RegistryException"/> carries its own status, <c>noRetry</c>, Retry-After and log
/// level: a new client error is a subclass or a throw site, never a new arm here.</item>
/// <item>D2: a <see cref="ForbiddenException"/> answers 403 to an authenticated request and 401 to an
/// anonymous one; <see cref="UnauthorizedException"/> (no valid credentials) is always 401.</item>
/// <item>The other arms are types the Registry does not own: cancellation, the DDB port's transient
/// errors, Kestrel's request errors (413 body too large) and <see cref="ArgumentException"/> from the
/// lower layers' validators (<c>Registry.Common</c>, <c>Registry.Ports</c>), which have no
/// <see cref="RegistryException"/> to throw.</item>
/// <item>Anything else is a server fault: 500 with a generic message (details are logged). Masking a
/// server bug as a 4xx is what this table must never do, so <see cref="InvalidOperationException"/> has
/// no arm: a client error is typed at its throw site.</item>
/// </list>
/// </summary>
public static class ApiExceptionClassifier
{
    /// <summary>Generic body for 500s, so engine details never leak to the client (details are logged).</summary>
    public const string InternalServerErrorMessage = "Internal server error";

    /// <summary>Message of Kestrel's mid-stream 413 (the declared length is checked earlier by
    /// <c>[EnforceContentLength]</c>).</summary>
    public const string RequestBodyTooLargeMessage = "Upload exceeds the maximum allowed size";

    /// <summary>
    /// Plan decision D2, the status of a denied permission: 403 for an authenticated caller, 401 for an
    /// anonymous one (signing in may help). The OGC authorization filter uses it too.
    /// </summary>
    public static int AccessDeniedStatus(bool authenticated) =>
        authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;

    /// <summary>
    /// Classifies <paramref name="ex"/> into the HTTP outcome of the request.
    /// </summary>
    /// <param name="ex">The exception that ended the request.</param>
    /// <param name="authenticated">Whether the request carries an authenticated identity (D2).</param>
    public static ApiErrorDescriptor Classify(Exception ex, bool authenticated)
    {
        // Client disconnected mid-request: not a server error (499 → 408; ASP.NET
        // Core has no 499 and no error envelope is produced for it).
        if (ex is OperationCanceledException)
            return new ApiErrorDescriptor(StatusCodes.Status408RequestTimeout, null, false, string.Empty, LogLevel.Debug);

        return ex switch
        {
            // D2: a missing permission is a 403 only for a caller who is signed in
            ForbiddenException e =>
                new ApiErrorDescriptor(AccessDeniedStatus(authenticated), null, e.NoRetry, e.Message, e.Level),
            RegistryException e => new ApiErrorDescriptor(e.StatusCode, e.RetryAfterSeconds, e.NoRetry, e.Message, e.Level),

            // The DDB port's transient contention: 503 + Retry-After so clients can retry. Ordered
            // before the 500 default: both derive from DdbException.
            DdbBusyException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 2, false, e.Message, LogLevel.Warning),
            DdbBuildInProgressException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 2, false, e.Message, LogLevel.Warning),

            // Kestrel: the body grew past MaxRequestBodySize while it was read
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                new ApiErrorDescriptor(StatusCodes.Status413PayloadTooLarge, null, true, RequestBodyTooLargeMessage, LogLevel.Information),

            // Validators below the Web layer (path rules, CommonUtils) report a bad argument
            ArgumentException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, false, e.Message, LogLevel.Information),

            // Unexpected: 500 with a generic message; full details stay in the log.
            _ => new ApiErrorDescriptor(StatusCodes.Status500InternalServerError, null, false, InternalServerErrorMessage, LogLevel.Error)
        };
    }
}
