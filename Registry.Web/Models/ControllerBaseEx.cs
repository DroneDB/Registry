using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Models;

public class ControllerBaseEx : ControllerBase
{

    protected IActionResult ExceptionResult(Exception ex)
    {

        // Do not retry if the quota is exceeded, the user is not authorized, or
        // the file extension is blocked by server policy
        var noRetry = ex is QuotaExceededException or UnauthorizedException or ExtensionBlockedException;

        var err = new ErrorResponse(ex.Message, noRetry);

        // Transient contention (native add/build busy): 503 + Retry-After so the
        // front-end retry set [0,429,502,503,504] can retry instead of treating
        // the 400 default as terminal. Mirrors ApiExceptionFilter for the same
        // exceptions when they escape to the pipeline instead of being caught here.
        if (ex is TransientException or DdbBusyException)
            return TransientStatusResult(err, ex);

        return ex switch
        {
            UnauthorizedException _ => Unauthorized(err),
            ConflictException _ => Conflict(err),
            NotFoundException _ => NotFound(err),
            _ => BadRequest(err)
        };
    }

    protected IActionResult ExceptionResult(Exception ex, bool noRetry)
    {
        var err = new ErrorResponse(ex.Message, noRetry);

        // Same transient mapping as the overload above; the caller-provided noRetry
        // flag is honored as-is.
        if (ex is TransientException or DdbBusyException)
            return TransientStatusResult(err, ex);

        return ex switch
        {
            UnauthorizedException _ => Unauthorized(err),
            ConflictException _ => Conflict(err),
            NotFoundException _ => NotFound(err),
            _ => BadRequest(err)
        };
    }

    /// <summary>
    /// Builds a 503 result for a transient contention error with the Retry-After
    /// response header, using the same seconds as
    /// <see cref="Registry.Web.Utilities.ApiExceptionFilter"/> (the exception type has
    /// no per-instance hint, so <see cref="DdbBusyException"/> uses the fixed 2s default).
    /// </summary>
    private IActionResult TransientStatusResult(ErrorResponse err, Exception ex)
    {
        var seconds = ex is TransientException tex ? tex.RetryAfterSeconds : 2;
        if (Response is { } response)
            response.Headers.RetryAfter = seconds.ToString();
        return new ObjectResult(err) { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }

}