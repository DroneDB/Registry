#nullable enable
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Registry.Web.Utilities;

/// <summary>
/// Global exception filter of the API controllers (registered once in <c>AddControllers</c>). It only
/// delivers what <see cref="ApiErrorResponder"/> decides, as an MVC result: errors of actions, action
/// filters and model binding. Everything outside MVC (middleware, minimal endpoints, resource filters)
/// reaches <see cref="ApiExceptionHandler"/>, which uses the same responder.
/// </summary>
public class ApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly ApiErrorResponder _responder;

    public ApiExceptionFilter(ApiErrorResponder responder)
    {
        _responder = responder;
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        var (descriptor, problem) = _responder.Describe(context.HttpContext, context.Exception);

        context.Result = problem is null
            ? new StatusCodeResult(descriptor.StatusCode)
            : ApiErrorResponder.ToActionResult(problem);
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
