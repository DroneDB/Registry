#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Utilities;

/// <summary>
/// <see cref="IExceptionHandler"/> of the whole pipeline (<c>UseExceptionHandler()</c>, RQ-05): errors that
/// the MVC filter never sees (middleware, minimal endpoints, the Hangfire branch, resource filters such as
/// <c>[EnforceContentLength]</c>, the SPA fallback) get the same ProblemDetails answer as an action,
/// built by <see cref="ApiErrorResponder"/>. A response that has already started is not handled here: the
/// middleware aborts it, so a truncated body is never reported as complete.
/// </summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ApiErrorResponder _responder;
    private readonly IProblemDetailsService _problemDetails;

    public ApiExceptionHandler(ApiErrorResponder responder, IProblemDetailsService problemDetails)
    {
        _responder = responder;
        _problemDetails = problemDetails;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var (descriptor, problem) = _responder.Describe(httpContext, exception);
        httpContext.Response.StatusCode = descriptor.StatusCode;

        if (problem is null)
            return true;

        var written = await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });

        // No writer accepts the request's Accept header (e.g. a browser navigation asking for HTML):
        // answer with the same body anyway rather than letting the exception escape the pipeline
        if (!written)
            await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null,
                ApiErrorResponder.ProblemJsonContentType, cancellationToken);

        return true;
    }
}
