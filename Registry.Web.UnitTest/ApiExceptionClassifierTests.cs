#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Exception-classifier characterization safety net (one row per exception type; the permission rows
/// classify an anonymous and an authenticated request).
///
/// These tests pin the exception-type → HTTP-response decision table that
/// <see cref="ApiExceptionClassifier"/> must produce after the two formerly
/// divergent mappings (ApiExceptionFilter and ControllerBaseEx.ExceptionResult)
/// were unified. Every row here is a client-observable status that must be
/// stable through the catch-block deletion waves and the residual-helper
/// removal. Rows marked "intentional flip" document the status that
/// deliberately changed at unification (the union mapping table):
/// server bugs are 500 (not masked 400), cancellations are 499/503 (were 408, before that 400),
/// build-in-progress is 503 (not 400), quota is 507/413/429 (not 400).
/// </summary>
public class ApiExceptionClassifierTests
{
    // ---- transient contention: 503 + Retry-After ----

    [Test]
    public void DdbBusy_MapsTo503WithTwoSecondRetry()
    {
        var d = ApiExceptionClassifier.Classify(new DdbBusyException(), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(2);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe(new DdbBusyException().Message);
        d.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void Transient_UsesPerInstanceRetryAfter()
    {
        var d = ApiExceptionClassifier.Classify(new TransientException("try again", retryAfterSeconds: 17), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(17);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("try again");
        d.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void DdbBuildInProgress_MapsTo503WithTwoSecondRetry()
    {
        var d = ApiExceptionClassifier.Classify(new DdbBuildInProgressException(), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(2);
        d.NoRetry.ShouldBeFalse();
        d.Level.ShouldBe(LogLevel.Warning);
    }

    // ---- cancellation: by what cancelled it ----

    [Test]
    public void OperationCanceled_ClientGone_Is499Debug_WithoutMessage()
    {
        var d = ApiExceptionClassifier.Classify(new OperationCanceledException("client gone"), authenticated: true,
            clientGone: true);

        d.StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
        d.RetryAfterSeconds.ShouldBeNull();
        d.Message.ShouldBeEmpty();
        d.Level.ShouldBe(LogLevel.Debug);
        ApiExceptionClassifier.IsClientGone(d).ShouldBeTrue();
    }

    [Test]
    public void DdbCanceled_ClientGone_Is499Debug()
    {
        var d = ApiExceptionClassifier.Classify(new DdbCanceledException("Canceled by the cancel token"),
            authenticated: false, clientGone: true);

        d.StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
        d.Level.ShouldBe(LogLevel.Debug);
    }

    [Test]
    public void OperationCanceled_ClientStillThere_Is503WithRetryAfter_LikeAShutdown()
    {
        // The host is stopping, a job's token, an internal timeout: the client waits for an answer
        var d = ApiExceptionClassifier.Classify(new TaskCanceledException("timed out"), authenticated: true);
        var shutdown = ApiExceptionClassifier.Classify(new DdbShutdownException("stopping"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        d.RetryAfterSeconds.ShouldBe(shutdown.RetryAfterSeconds);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe(ApiExceptionClassifier.CanceledMessage);
        d.Level.ShouldBe(LogLevel.Warning);
        ApiExceptionClassifier.IsClientGone(d).ShouldBeFalse();
    }

    [Test]
    public void FromTheRequest_ReadsWhetherTheClientIsGone()
    {
        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };

        ApiExceptionClassifier.Classify(new OperationCanceledException(), context).StatusCode
            .ShouldBe(StatusCodes.Status503ServiceUnavailable);
        aborted.Cancel();
        ApiExceptionClassifier.Classify(new OperationCanceledException(), context).StatusCode
            .ShouldBe(StatusCodes.Status499ClientClosedRequest);
        // Any other error is classified as before, whether the client is there or not
        ApiExceptionClassifier.Classify(new DdbBusyException(), context).StatusCode
            .ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    // ---- client errors: 4xx ----

    [Test]
    public void ExtensionBlocked_Single_Is400NoRetryAndNamesTheFile()
    {
        var ex = new ExtensionBlockedException("virus.exe", allowListMode: false);
        var d = ApiExceptionClassifier.Classify(ex, authenticated: true);

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
        var ex = new ExtensionBlockedException(["a.exe", "b.bat"], new ImportSettings());
        var d = ApiExceptionClassifier.Classify(ex, authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        d.NoRetry.ShouldBeTrue();
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void HeavyToolNotFound_MapsTo400()
    {
        var d = ApiExceptionClassifier.Classify(new HeavyToolNotFoundException("tool 'x' unknown"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("tool 'x' unknown");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void BadArguments_MapTo400()
    {
        ApiExceptionClassifier.Classify(new BadRequestException("bad input"), authenticated: true).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        ApiExceptionClassifier.Classify(new ArgumentException("bad arg"), authenticated: true).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var d = ApiExceptionClassifier.Classify(new ArgumentException("bad arg"), authenticated: true);
        d.NoRetry.ShouldBeFalse();
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void Unauthorized_MapsTo401NoRetry()
    {
        var d = ApiExceptionClassifier.Classify(new UnauthorizedException("who"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        d.NoRetry.ShouldBeTrue();
        d.Message.ShouldBe("who");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void Unauthorized_IsA401EvenForAnAuthenticatedRequest()
    {
        // No valid credentials (a deleted or deactivated user's token): the Hub must sign out
        ApiExceptionClassifier.Classify(new UnauthorizedException("gone"), authenticated: true).StatusCode
            .ShouldBe(StatusCodes.Status401Unauthorized);
        ApiExceptionClassifier.Classify(new UnauthorizedException("who"), authenticated: false).StatusCode
            .ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public void Forbidden_D2_403WhenAuthenticated401WhenAnonymous()
    {
        var authenticated = ApiExceptionClassifier.Classify(new ForbiddenException("no access"), authenticated: true);
        authenticated.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        authenticated.NoRetry.ShouldBeTrue();
        authenticated.Message.ShouldBe("no access");
        authenticated.Level.ShouldBe(LogLevel.Information);

        var anonymous = ApiExceptionClassifier.Classify(new ForbiddenException("no access"), authenticated: false);
        anonymous.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        anonymous.NoRetry.ShouldBeTrue();
        anonymous.Message.ShouldBe("no access");
    }

    [Test]
    public void Forbidden_KeepsAnExplicitRetryableFlag()
    {
        ApiExceptionClassifier.Classify(new ForbiddenException("Access denied", noRetry: false), authenticated: true)
            .NoRetry.ShouldBeFalse();
    }

    [Test]
    public void NotFound_MapsTo404()
    {
        var d = ApiExceptionClassifier.Classify(new NotFoundException("gone"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("gone");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void Conflict_MapsTo409()
    {
        var d = ApiExceptionClassifier.Classify(new ConflictException("taken"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe("taken");
        d.Level.ShouldBe(LogLevel.Information);
    }

    // ---- resource limits ----

    [Test]
    public void QuotaExceeded_MapsTo507NoRetry()
    {
        var d = ApiExceptionClassifier.Classify(new QuotaExceededException(1L, 2L), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status507InsufficientStorage);
        d.NoRetry.ShouldBeTrue();
        d.Level.ShouldBe(LogLevel.Warning);
    }

    [Test]
    public void HeavyTaskQuota_MapsToHttpStatus413And429NoRetry()
    {
        var tooLarge = ApiExceptionClassifier.Classify(new HeavyTaskQuotaException(HeavyTaskQuotaCode.TooLarge, "file too large"), authenticated: true);
        tooLarge.StatusCode.ShouldBe(413);
        tooLarge.NoRetry.ShouldBeTrue();
        tooLarge.Message.ShouldBe("file too large");
        tooLarge.Level.ShouldBe(LogLevel.Warning);

        var exceeded = ApiExceptionClassifier.Classify(new HeavyTaskQuotaException(HeavyTaskQuotaCode.Exceeded, "too many"), authenticated: true);
        exceeded.StatusCode.ShouldBe(429);
        exceeded.NoRetry.ShouldBeTrue();
    }

    [Test]
    public void PayloadTooLarge_MapsTo413NoRetry()
    {
        var d = ApiExceptionClassifier.Classify(new PayloadTooLargeException("too big"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        d.NoRetry.ShouldBeTrue();
        d.Message.ShouldBe("too big");
        d.Level.ShouldBe(LogLevel.Information);
    }

    [Test]
    public void KestrelBodyTooLarge_MapsTo413NoRetry()
    {
        var d = ApiExceptionClassifier.Classify(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        d.NoRetry.ShouldBeTrue();
        d.Message.ShouldBe(ApiExceptionClassifier.RequestBodyTooLargeMessage);
    }

    [Test]
    public void OtherKestrelRequestErrors_StayServerFaults()
    {
        // Only the 413 is a client condition with a known message; nothing else is widened
        ApiExceptionClassifier.Classify(new BadHttpRequestException("bad", StatusCodes.Status400BadRequest),
            authenticated: true).StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public void TooManyRequests_MapsTo429WithRetryAfter()
    {
        var d = ApiExceptionClassifier.Classify(new TooManyRequestsException("slow down", 10), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        d.RetryAfterSeconds.ShouldBe(10);
        d.NoRetry.ShouldBeFalse();
    }

    [Test]
    public void EveryRegistryException_MapsToItsOwnStatus()
    {
        // The vocabulary carries its status: the classifier has one arm for all of it
        RegistryException[] all =
        [
            new BadRequestException("a"), new UnauthorizedException("b"), new ForbiddenException("c"),
            new NotFoundException("d"), new ConflictException("e"), new PayloadTooLargeException("f"),
            new TooManyRequestsException("g", 1), new TransientException("h"), new QuotaExceededException("i"),
            new ExtensionBlockedException("x.exe", allowListMode: false),
            new HeavyTaskQuotaException(HeavyTaskQuotaCode.TooLarge, "j"), new HeavyToolNotFoundException("k")
        ];

        foreach (var ex in all)
        {
            var d = ApiExceptionClassifier.Classify(ex, authenticated: true);
            d.StatusCode.ShouldBe(ex.StatusCode, ex.GetType().Name);
            d.NoRetry.ShouldBe(ex.NoRetry, ex.GetType().Name);
            d.Message.ShouldBe(ex.Message, ex.GetType().Name);
            d.Level.ShouldBe(ex.Level, ex.GetType().Name);
        }
    }

    // ---- the default arm: 500 with a generic, non-leaking message ----

    [Test]
    public void Unexpected_ServerErrorsMapTo500WithGenericMessage()
    {
        var secretMessage = "sqlite: database is locked at handle 0x1F3";
        var d = ApiExceptionClassifier.Classify(new InvalidOperationException(secretMessage), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        d.RetryAfterSeconds.ShouldBeNull();
        d.NoRetry.ShouldBeFalse();
        d.Message.ShouldBe(ApiExceptionClassifier.InternalServerErrorMessage);
        d.Message.ShouldNotContain(secretMessage);
        d.Level.ShouldBe(LogLevel.Error);
    }

    [Test]
    public void InvalidOperationException_IsNeverAClientError()
    {
        // Client errors are typed at their throw site; the classifier is not widened
        ApiExceptionClassifier.Classify(new InvalidOperationException("Merge conflicts detected"), authenticated: true)
            .StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public void GenericDdbException_MapsTo500Not400()
    {
        // Plain DdbException (unmapped native result) is a server fault: 500.
        var d = ApiExceptionClassifier.Classify(new DdbException("add failed: busy again"), authenticated: true);

        d.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        d.Message.ShouldBe(ApiExceptionClassifier.InternalServerErrorMessage);
        d.Level.ShouldBe(LogLevel.Error);
    }

}
