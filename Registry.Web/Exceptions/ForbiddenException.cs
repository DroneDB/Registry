using System;

namespace Registry.Web.Exceptions;

/// <summary>
/// The caller is authenticated but not allowed to perform the operation (HTTP 403).
/// Distinct from <see cref="UnauthorizedException"/>, which reports 401.
/// </summary>
public class ForbiddenException : Exception
{
    /// <summary>Surfaced as <c>ErrorResponse.NoRetry</c>: true when the same payload can never succeed.</summary>
    public bool NoRetry { get; }

    public ForbiddenException(string message, bool noRetry = false) : base(message)
    {
        NoRetry = noRetry;
    }
}
