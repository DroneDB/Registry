using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;

namespace Registry.Web.Utilities;

/// <summary>
/// Global exception-to-HTTP filter for API controllers (added during the
/// exception-handling unification). Registered once via <c>services.AddControllers(o =>
/// o.Filters.Add&lt;ApiExceptionFilter&gt;())</c>; classification rules live in
/// <see cref="ApiExceptionClassifier"/>, the single mapping table for every managed
/// exception. The legacy per-action <c>ControllerBaseEx.ExceptionResult</c> helper was
/// deleted during the unification — no per-controller error wrappers remain.
/// </summary>
public class ApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        var ex = context.Exception;
        var d = ApiExceptionClassifier.Classify(ex);

        // Every incident is logged at the level chosen by the classifier (500s at
        // Error with the full exception, 408 client-disconnects at Debug, etc.).
        _logger.Log(d.Level, ex, "Exception in {Path}", context.HttpContext.Request.Path);

        if (d.RetryAfterSeconds.HasValue)
            context.HttpContext.Response.Headers.RetryAfter = d.RetryAfterSeconds.Value.ToString();

        // 408 (client went away) is produced without a body: nobody is left to read it.
        context.Result = d.StatusCode == StatusCodes.Status408RequestTimeout
            ? new StatusCodeResult(StatusCodes.Status408RequestTimeout)
            : new ObjectResult(new ErrorResponse(d.Message, d.NoRetry))
            { StatusCode = d.StatusCode };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
