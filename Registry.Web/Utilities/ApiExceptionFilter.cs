using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;

namespace Registry.Web.Utilities;

/// <summary>
/// Global exception -> HTTP status mapping for API controllers (see ImproveParallelWrites plan,
/// workstream 04 §5.3). Registered once via <c>services.AddControllers(o =>
/// o.Filters.Add&lt;ApiExceptionFilter&gt;())</c>; classification rules live here once instead of
/// being duplicated across ~49 per-action <c>catch (Exception ex) { return 500; }</c> blocks.
/// Additive: existing controller-level try/catch + <see cref="ControllerBaseEx.ExceptionResult"/>
/// still work as-is for actions that already handle their own errors; this filter only fires for
/// exceptions that escape the action unhandled.
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

        // Client disconnected mid-request: not a server error (see 02-target-architecture.md §8
        // for the 499->408 rationale; ASP.NET Core has no 499, 408 is the closest standard code).
        if (ex is OperationCanceledException)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status408RequestTimeout);
            context.ExceptionHandled = true;
            return Task.CompletedTask;
        }

        var (status, retryAfterSeconds) = ex switch
        {
            DdbBusyException => (StatusCodes.Status503ServiceUnavailable, (int?)2),
            TransientException tex => (StatusCodes.Status503ServiceUnavailable, (int?)tex.RetryAfterSeconds),
            DdbBuildInProgressException => (StatusCodes.Status503ServiceUnavailable, (int?)2),
            BadRequestException => (StatusCodes.Status400BadRequest, null),
            ExtensionBlockedException => (StatusCodes.Status400BadRequest, null),
            UnauthorizedException => (StatusCodes.Status401Unauthorized, null),
            NotFoundException => (StatusCodes.Status404NotFound, null),
            ConflictException => (StatusCodes.Status409Conflict, null),
            QuotaExceededException => (StatusCodes.Status507InsufficientStorage, null),
            _ => (StatusCodes.Status500InternalServerError, (int?)null)
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(ex, "Unhandled exception in {Path}", context.HttpContext.Request.Path);
        }

        var noRetry = ex is QuotaExceededException or UnauthorizedException or ExtensionBlockedException;

        if (retryAfterSeconds.HasValue)
            context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString();

        context.Result = new ObjectResult(new ErrorResponse(ex.Message, noRetry))
        {
            StatusCode = status
        };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
