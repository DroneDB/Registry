using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Phase D3 of ImproveParallelWrites — characterization safety net.
///
/// These tests pin the exception-type → HTTP-response decision table that
/// <see cref="ApiExceptionClassifier"/> must produce after the two formerly
/// divergent mappings (ApiExceptionFilter and ControllerBaseEx.ExceptionResult)
/// were unified. Every row here is a client-observable status that must be
/// stable through the catch-block deletion waves (D4) and the residual-helper
/// removal (D5). Rows marked "intentional flip" document the status that
/// deliberately changed at unification (the union table in PLAN.md phase D):
/// server bugs are 500 (not masked 400), cancellations are 408 (not 400),
/// build-in-progress is 503 (not 400), quota is 507/413/429 (not 400).
/// </summary>
public class ApiExceptionClassifierTests
{
    // ---- transient contention: 503 + Retry-After ----

    [Test]
    public void DdbBusy_MapsTo503WithTwoSecondRetry()
    {
        var d = ApiExceptionClassifier.Classify(new DdbBusyException());

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(2);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe(new DdbBusyException().Message);
        d.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void Transient_UsesPerInstanceRetryAfter()
    {
        var d = ApiExceptionClassifier.Classify(new TransientException("try again", retryAfterSeconds: 17));

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(17);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("try again");
        d.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void DdbBuildInProgress_MapsTo503WithTwoSecondRetry()
    {
        var d = ApiExceptionClassifier.Classify(new DdbBuildInProgressException());

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(2);
        d.NoRetry.ShouldBeFalse();
        d.Level.ShouldBe(LogLevel.Warning);
    }

    // ---- cancellation: 408, quiet ----

    [Test]
    public void OperationCanceled_MapsTo408Debug()
    {
        var d = ApiExceptionClassifier.Classify(new OperationCanceledException("client gone"));

        d.StatusCode.ShouldBe(StatusCodes.Status408RequestTimeout);
        d.RetryAfterSeconds.ShouldBeNull();
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBeEmpty();
        d.Level.ShouldBe(LogLevel.Debug);
    }

    [Test]
    public void TaskCanceled_MapsTo408Debug()
    {
        var d = ApiExceptionClassifier.Classify(new TaskCanceledException("client gone"));

        d.StatusCode.ShouldBe(StatusCodes.Status408RequestTimeout);
        d.Level.ShouldBe(LogLevel.Debug);
    }

    // ---- client errors: 4xx ----

    [Test]
    public void ExtensionBlocked_Single_Is400NoRetryAndNamesTheFile()
    {
        var ex = new ExtensionBlockedException("virus.exe", new ImportSettings(), allowListMode: false);
        var d = ApiExceptionClassifier.Classify(ex);

        d.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        d.RetryAfterSeconds.ShouldBeNull();
        d.NoRetry.ShouldBeTrue();
        d.Message.ShouldBe(ex.Message);
        d.Message.ShouldContain("virus.exe");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void ExtensionBlocked_Batch_Is400NoRetry()
    {
        var ex = new ExtensionBlockedException(new[] { "a.exe", "b.bat" }, new ImportSettings());
        var d = ApiExceptionClassifier.Classify(ex);

        d.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        d.NoRetry.ShouldBeTrue();
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void HeavyToolNotFound_MapsTo400()
    {
        var d = ApiExceptionClassifier.Classify(new HeavyToolNotFoundException("tool 'x' unknown"));

        d.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("tool 'x' unknown");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void BadArguments_MapTo400()
    {
        ApiExceptionClassifier.Classify(new BadRequestException("bad input")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        ApiExceptionClassifier.Classify(new ArgumentException("bad arg")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var d = ApiExceptionClassifier.Classify(new ArgumentException("bad arg"));
        d.NoRetry.ShouldBeFalse();
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void Unauthorized_MapsTo401NoRetry()
    {
        var d = ApiExceptionClassifier.Classify(new UnauthorizedException("who"));

        d.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        d.NoRetry.ShouldBeTrue();
        d.Message.ShouldBe("who");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void NotFound_MapsTo404()
    {
        var d = ApiExceptionClassifier.Classify(new NotFoundException("gone"));

        d.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("gone");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void Conflict_MapsTo409()
    {
        var d = ApiExceptionClassifier.Classify(new ConflictException("taken"));

        d.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("taken");
        d.Level.ShouldBe(LogLevel.Information);
    }

    // ---- resource limits ----

    [Test]
    public void QuotaExceeded_MapsTo507NoRetry()
    {
        var d = ApiExceptionClassifier.Classify(new QuotaExceededException(1L, 2L));

        d.StatusCode.ShouldBe(StatusCodes.Status507InsufficientStorage);
        d.NoRetry.ShouldBeTrue();
        d.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void HeavyTaskQuota_MapsToHttpStatus413And429NoRetry()
    {
        var tooLarge = ApiExceptionClassifier.Classify(new HeavyTaskQuotaException(HeavyTaskQuotaCode.TooLarge, "file too large"));
        tooLarge.StatusCode.ShouldBe(413);
        tooLarge.NoRetry.ShouldBeTrue();
        tooLarge.Message.ShouldBe("file too large");
        tooLarge.Level.ShouldBe(LogLevel.Warning);

        var exceeded = ApiExceptionClassifier.Classify(new HeavyTaskQuotaException(HeavyTaskQuotaCode.Exceeded, "too many"));
        exceeded.StatusCode.ShouldBe(429);
        exceeded.NoRetry.ShouldBeTrue();
    }

    // ---- the default arm: 500 with a generic, non-leaking message ----

    [Test]
    public void Unexpected_ServerErrorsMapTo500WithGenericMessage()
    {
        var secretMessage = "sqlite: database is locked at handle 0x1F3";
        var d = ApiExceptionClassifier.Classify(new InvalidOperationException(secretMessage));

        d.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        d.RetryAfterSeconds.ShouldBeNull();
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe(ApiExceptionClassifier.InternalServerErrorMessage);
        d.Message.ShouldNotContain(secretMessage);
        d.Level.ShouldBe(LogLevel.Error);
    }

    [Test]
    public void GenericDdbException_MapsTo500Not400()
    {
        // Plain DdbException (unmapped native result) is a server fault: 500.
        var d = ApiExceptionClassifier.Classify(new DdbException("add failed: busy again"));

        d.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        d.Message.ShouldBe(ApiExceptionClassifier.InternalServerErrorMessage);
        d.Level.ShouldBe(LogLevel.Error);
    }

}
