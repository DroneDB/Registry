using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// The caller may not perform the operation: a failed access check, an admin-only action, a feature
/// disabled on this server. <see cref="Utilities.ApiExceptionClassifier"/> answers
/// 403 to an authenticated request and 401 to an anonymous one (signing in may help). Distinct from
/// <see cref="UnauthorizedException"/>, which means "no valid credentials" and is always 401.
/// </summary>
public class ForbiddenException : RegistryException
{
    /// <param name="message">Client-facing message.</param>
    /// <param name="noRetry">Surfaced as <c>noRetry</c>; true by default: the same request is denied again
    /// until the caller's permissions change.</param>
    public ForbiddenException(string message, bool noRetry = true)
        : base(StatusCodes.Status403Forbidden, message, noRetry)
    {
    }
}
