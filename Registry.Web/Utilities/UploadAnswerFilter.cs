using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Registry.Web.Utilities;

/// <summary>
/// The exception filter of the upload actions (<c>[TypeFilter(typeof(UploadAnswerFilter))]</c>): the error body of
/// <see cref="ApiErrorResponder"/>, with the <c>noRetry</c> flag of <see cref="UploadRetryRule"/>. It runs before the
/// global <see cref="ApiExceptionFilter"/> and marks the exception handled, so the body is written once.
/// </summary>
public sealed class UploadAnswerFilter : IAsyncExceptionFilter
{
    private readonly ApiErrorResponder _responder;

    public UploadAnswerFilter(ApiErrorResponder responder)
    {
        _responder = responder;
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        var (descriptor, problem) = _responder.Describe(context.HttpContext, context.Exception);

        if (problem != null)
            problem.Extensions[ApiErrorResponder.NoRetryMember] = UploadRetryRule.NoRetry(descriptor, context.Exception);

        context.Result = problem is null
            ? new StatusCodeResult(descriptor.StatusCode)
            : ApiErrorResponder.ToActionResult(problem);
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
