using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class ControllerBaseExExceptionResultTests
{
    // Thin controller exposing the protected mapping for testing.
    private sealed class ProbeController : ControllerBaseEx
    {
        public IActionResult Map(Exception ex) => ExceptionResult(ex);

        public IActionResult MapNoRetry(Exception ex) => ExceptionResult(ex, noRetry: true);
    }

    private static ProbeController CreateController() => new()
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [Test]
    public void ExceptionResult_TransientException_Returns503WithRetryAfter()
    {
        var controller = CreateController();

        var result = (ObjectResult)controller.Map(new TransientException("index is busy", retryAfterSeconds: 7));

        result.StatusCode.ShouldBe(503);
        result.Value.ShouldBeOfType<ErrorResponse>().NoRetry.ShouldBeFalse();
        controller.ControllerContext.HttpContext.Response.Headers.RetryAfter[0].ShouldBe("7");
    }

    [Test]
    public void ExceptionResult_DdbBusyException_Returns503WithDefaultRetryAfter()
    {
        var controller = CreateController();

        var result = (ObjectResult)controller.Map(new DdbBusyException("locked"));

        result.StatusCode.ShouldBe(503);
        result.Value.ShouldBeOfType<ErrorResponse>().NoRetry.ShouldBeFalse();
        // Same fixed 2s default ApiExceptionFilter uses for DdbBusyException.
        controller.ControllerContext.HttpContext.Response.Headers.RetryAfter[0].ShouldBe("2");
    }

    [Test]
    public void ExceptionResult_Transient_NoRetryOverload_KeepsCallerNoRetryAndSetsHeader()
    {
        var controller = CreateController();

        var result = (ObjectResult)controller.MapNoRetry(new TransientException("index is busy"));

        result.StatusCode.ShouldBe(503);
        result.Value.ShouldBeOfType<ErrorResponse>().NoRetry.ShouldBeTrue();
        controller.ControllerContext.HttpContext.Response.Headers.RetryAfter[0].ShouldBe("2");
    }

    [Test]
    public void ExceptionResult_NonTransientTypes_KeepExistingStatusCodes()
    {
        var controller = CreateController();

        (controller.Map(new NotFoundException("nope")) as ObjectResult).StatusCode.ShouldBe(404);
        (controller.Map(new ConflictException("clash")) as ObjectResult).StatusCode.ShouldBe(409);
        (controller.Map(new ArgumentException("bad")) as ObjectResult).StatusCode.ShouldBe(400);

        // No Retry-After on non-transient errors.
        controller.ControllerContext.HttpContext.Response.Headers.ShouldNotContainKey("Retry-After");
    }
}
