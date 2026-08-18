using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// Describes the HTTP response a managed exception maps to (Phase D of
/// ImproveParallelWrites): status, optional Retry-After hint, the NoRetry flag
/// surfaced to clients via <c>ErrorResponse.NoRetry</c>, the client-facing
/// message, and the level at which the server should log the incident.
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
/// Single source of truth for the managed-exception → HTTP-response mapping
/// (Phase D of ImproveParallelWrites). Replaces the two previously divergent
/// tables (<see cref="ApiExceptionFilter"/> and the former
/// <c>ControllerBaseEx.ExceptionResult</c>); both call sites now delegate here.
/// The mapping is the union of every per-controller interpretation that
/// existed pre-unification (see PLAN.md phase D table). Unlisted exception
/// types deliberately fall through to 500 + "Internal server error": masking
/// server bugs as 400 was the bug being removed.
/// </summary>
public static class ApiExceptionClassifier
{
    /// <summary>Generic body for 500s, so engine details never leak to the client (details are logged).</summary>
    public const string InternalServerErrorMessage = "Internal server error";

    /// <summary>
    /// Classifies <paramref name="ex"/> into the HTTP outcome everyone (global
    /// filter, legacy per-action helper) must produce.
    /// </summary>
    public static ApiErrorDescriptor Classify(Exception ex)
    {
        // Client disconnected mid-request: not a server error (499 → 408; ASP.NET
        // Core has no 499 and no error envelope is produced for it).
        if (ex is OperationCanceledException)
            return new ApiErrorDescriptor(StatusCodes.Status408RequestTimeout, null, false, string.Empty, LogLevel.Debug);

        // Note: cases are ordered by specificity; DdbBusy/DdbBuildInProgress derive
        // from DdbException but must not collapse into the default 500 arm.
        return ex switch
        {
            // Transient contentment: 503 + Retry-After so clients can retry.
            DdbBusyException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 2, false, e.Message, LogLevel.Warning),
            TransientException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, e.RetryAfterSeconds, false, e.Message, LogLevel.Warning),
            DdbBuildInProgressException e => new ApiErrorDescriptor(StatusCodes.Status503ServiceUnavailable, 2, false, e.Message, LogLevel.Warning),

            // Client errors: 4xx, safe to log at Information.
            ExtensionBlockedException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, true, e.Message, LogLevel.Information),
            HeavyToolNotFoundException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, false, e.Message, LogLevel.Information),
            BadRequestException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, false, e.Message, LogLevel.Information),
            ArgumentException e => new ApiErrorDescriptor(StatusCodes.Status400BadRequest, null, false, e.Message, LogLevel.Information),
            UnauthorizedException e => new ApiErrorDescriptor(StatusCodes.Status401Unauthorized, null, true, e.Message, LogLevel.Information),
            NotFoundException e => new ApiErrorDescriptor(StatusCodes.Status404NotFound, null, false, e.Message, LogLevel.Information),
            ConflictException e => new ApiErrorDescriptor(StatusCodes.Status409Conflict, null, false, e.Message, LogLevel.Information),

            // Resource limits: non-retryable with the same payload.
            QuotaExceededException e => new ApiErrorDescriptor(StatusCodes.Status507InsufficientStorage, null, true, e.Message, LogLevel.Warning),
            HeavyTaskQuotaException e => new ApiErrorDescriptor((int)e.Code, null, true, e.Message, LogLevel.Warning),

            // Unexpected: 500 with a generic message; full details stay in the log.
            _ => new ApiErrorDescriptor(StatusCodes.Status500InternalServerError, null, false, InternalServerErrorMessage, LogLevel.Error)
        };
    }
}
