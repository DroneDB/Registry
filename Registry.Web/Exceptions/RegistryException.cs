#nullable enable
using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Exceptions;

/// <summary>
/// Base of every error the Registry raises on purpose (RQ-05). The exception carries its own HTTP
/// outcome, so a throw site states what went wrong and <see cref="Utilities.ApiExceptionClassifier"/>
/// maps every subclass with one rule; a new client error needs a subclass or a throw, never a new
/// classifier arm. Anything that is not a <see cref="RegistryException"/> (and not one of the few
/// framework/port types the classifier knows) is a server fault: 500 with a generic message.
/// </summary>
public abstract class RegistryException : Exception
{
    protected RegistryException(int statusCode, string message, bool noRetry = false,
        Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
        NoRetry = noRetry;
    }

    /// <summary>HTTP status of the response.</summary>
    public int StatusCode { get; }

    /// <summary>Surfaced as the <c>noRetry</c> member of the ProblemDetails body: true when the same
    /// request can never succeed.</summary>
    public bool NoRetry { get; init; }

    /// <summary>Seconds advertised in <c>Retry-After</c> (transient errors only).</summary>
    public virtual int? RetryAfterSeconds => null;

    /// <summary>Level of the one server-side log line: client errors at Information, server-side
    /// conditions (5xx) at Warning.</summary>
    public virtual LogLevel Level =>
        StatusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Warning : LogLevel.Information;
}
