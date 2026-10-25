#nullable enable
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// The caller has no valid credentials (HTTP 401, whatever the request carries): a failed login, a
/// token whose user no longer exists, a refresh refused to a deactivated user. A 401 makes the Hub
/// sign the user out, so a caller that is authenticated but lacks a permission gets
/// <see cref="ForbiddenException"/> instead.
/// </summary>
public class UnauthorizedException : RegistryException
{
    /// <param name="message">Client-facing message.</param>
    /// <param name="noRetry">Surfaced as <c>noRetry</c>; true by default, as 401s have always been reported.</param>
    public UnauthorizedException(string message, bool noRetry = true)
        : base(StatusCodes.Status401Unauthorized, message, noRetry)
    {
    }
}
