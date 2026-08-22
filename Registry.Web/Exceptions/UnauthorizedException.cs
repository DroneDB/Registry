using System;

namespace Registry.Web.Exceptions;

public class UnauthorizedException : Exception
{
    /// <summary>Surfaced as <c>ErrorResponse.NoRetry</c>; defaults to true, which is how 401s have always been reported.</summary>
    public bool NoRetry { get; init; } = true;

    public UnauthorizedException(string message) : base(message)
    {
        //
    }
}