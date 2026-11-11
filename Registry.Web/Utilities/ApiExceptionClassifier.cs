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
/// The one exception → HTTP mapping of the Registry API. Used by <see cref="ApiErrorResponder"/>
/// (MVC filter, <c>IExceptionHandler</c>) and by the OGC filter for its status codes.
/// <list type="bullet">
/// <item>Every <see cref="RegistryException"/> carries its own status, <c>noRetry</c>, Retry-After and log
/// level: a new client error is a subclass or a throw site, never a new arm here.</item>
/// <item>A <see cref="ForbiddenException"/> answers 403 to an authenticated request and 401 to an
/// anonymous one; <see cref="UnauthorizedException"/> (no valid credentials) is always 401.</item>
/// <item>Cancellation: 499 without a body when the client went away, 503 + Retry-After otherwise.</item>
/// <item>The other arms are types the Registry does not own: the DDB port's typed
/// errors (transient: 503; invalid arguments and a file its reader refused: 400; not found: 404), Kestrel's request errors (413 body too large) and <see cref="ArgumentException"/> from the
/// lower layers' validators (<c>Registry.Common</c>, <c>Registry.Ports</c>), which have no
/// <see cref="RegistryException"/> to throw.</item>
/// <item>Anything else is a server fault: 500 with a generic message (details are logged). Masking a
/// server bug as a 4xx is what this table must never do, so <see cref="InvalidOperationException"/> has
/// no arm: a client error is typed at its throw site.</item>
/// </list>
/// </summary>
public static class ApiExceptionClassifier
{
    // A stopping host is back (or replaced) within seconds
    private const int ShutdownRetryAfterSeconds = 5;

    /// <summary>Generic body for 500s, so engine details never leak to the client (details are logged).</summary>
    public const string InternalServerErrorMessage = "Internal server error";

    /// <summary>Message of Kestrel's mid-stream 413 (the declared length is checked earlier by
    /// <c>[EnforceContentLength]</c>).</summary>
    public const string RequestBodyTooLargeMessage = "Upload exceeds the maximum allowed size";

    /// <summary>
    /// The status of a denied permission: 403 for an authenticated caller, 401 for an
    /// anonymous one (signing in may help). The OGC authorization filter uses it too.
    /// </summary>
    public static int AccessDeniedStatus(bool authenticated) =>
        authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;

    /// <summary>Body of a cancellation the client did not ask for (the host stopping, a job's token, a timeout).</summary>
    public const string CanceledMessage = "The operation was canceled; retry shortly.";

    /// <summary>
    /// The client went away: nothing is written for it, the status (499, nginx's "client closed request",
    /// as ASP.NET Core's exception handler middleware uses) only reaches the server's logs.
    /// </summary>
    public static bool IsClientGone(ApiErrorDescriptor descriptor) =>
        descriptor.StatusCode == StatusCodes.Status499ClientClosedRequest;

    /// <summary>
    /// Logs an incident once, at the classifier's level. A client error (4xx: a denial, a missing dataset, a bad
    /// argument) is expected traffic: one line with the method, the route, the status and the message, and no
    /// stack. A server fault (5xx) keeps its exception.
    /// </summary>
    public static void Log(ILogger logger, ApiErrorDescriptor descriptor, Exception exception, HttpRequest request)
    {
        if (descriptor.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.Log(descriptor.Level, exception, "Exception in {Path}", request.Path);
            return;
        }

        // The client that went away has no message: nobody wrote one
        var message = IsClientGone(descriptor) ? "the client closed the request" : descriptor.Message;
        logger.Log(descriptor.Level, "{Method} {Path} -> {StatusCode}: {Message}", request.Method, request.Path,
            descriptor.StatusCode, message);
    }

    /// <summary>
    /// Classifies <paramref name="ex"/> for <paramref name="request"/>: whether it is authenticated and
    /// whether its client went away (<c>RequestAborted</c>).
    /// </summary>
    public static ApiErrorDescriptor Classify(Exception ex, HttpContext request) =>
        Classify(ex, request.User.Identity?.IsAuthenticated == true, request.RequestAborted.IsCancellationRequested);

    /// <summary>
    /// Classifies <paramref name="ex"/> into the HTTP outcome of the request.
    /// </summary>
    /// <param name="ex">The exception that ended the request.</param>
    /// <param name="authenticated">Whether the request carries an authenticated identity.</param>
    /// <param name="clientGone">Whether the request's client went away (its <c>RequestAborted</c> is canceled).</param>
    public static ApiErrorDescriptor Classify(Exception ex, bool authenticated, bool clientGone = false)
    {
        // A cancellation is not a server fault. The client that went away reads nothing: 499, quiet. One
        // it did not ask for (the host is stopping, a job's or a timeout's token) can succeed later: 503 +
        // Retry-After, like DdbShutdownException
        if (ex is OperationCanceledException)
            return clientGone
                ? new ApiErrorDescriptor(StatusCodes.Status499ClientClosedRequest, null, false, string.Empty, LogLevel.Debug)
                : new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, ShutdownRetryAfterSeconds, false,
                    CanceledMessage, LogLevel.Warning);

        return ex switch
        {
            // A missing permission is a 403 only for a caller who is signed in
            ForbiddenException e =>
                new ApiErrorDescriptor(AccessDeniedStatus(authenticated), null, e.NoRetry, e.Message, e.Level),
            RegistryException e => new ApiErrorDescriptor(e.StatusCode, e.RetryAfterSeconds, e.NoRetry, e.Message, e.Level),

            // The DDB port's transient contention: 503 + Retry-After so clients can retry. Ordered
            // before the 500 default: both derive from DdbException.
            DdbBusyException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 2, false, e.Message, LogLevel.Warning),
            DdbBuildInProgressException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 2, false, e.Message, LogLevel.Warning),
            // libddb ABI 2.0: a transient failure (disk full, tool timeout) and a process that
            // is shutting down can succeed later; an invalid argument or a missing build never can
            DdbRetryableException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 60, false, e.Message, LogLevel.Warning),
            DdbShutdownException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, ShutdownRetryAfterSeconds, false, e.Message, LogLevel.Warning),
            DdbInvalidArgumentsException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, true, e.Message, LogLevel.Information),
            // A file its reader refused (an upload that is not what its type says): the client's data
            DdbInvalidContentException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, true, e.Message, LogLevel.Information),
            DdbNotFoundException e => new ApiErrorDescriptor(StatusCodes.Status404NotFound, null, true, e.Message, LogLevel.Information),

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
