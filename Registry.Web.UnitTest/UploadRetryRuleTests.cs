using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-XC-12, FR-UPL-07: an upload answer of 401, 403, 404, 409, 413 or 507 carries <c>noRetry: true</c>, but for the
/// two recoverable 409s (chunks missing, the same upload still running); the classifier keeps each exception's own
/// flag for the other endpoints (a 404 while a build is pending is retried).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UploadRetryRule"
/// </summary>
[TestFixture]
public class UploadRetryRuleTests
{
    private static bool NoRetry(Exception error, bool authenticated = true) =>
        UploadRetryRule.NoRetry(ApiExceptionClassifier.Classify(error, authenticated), error);

    [Test]
    public void FinalStatuses_AreTheSixOfTheSpec()
    {
        foreach (var status in new[] { 401, 403, 404, 409, 413, 507 })
            UploadRetryRule.IsFinalStatus(status).ShouldBeTrue(status.ToString(CultureInfo.InvariantCulture));
        foreach (var status in new[] { 200, 201, 400, 429, 500, 503 })
            UploadRetryRule.IsFinalStatus(status).ShouldBeFalse(status.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public void AnUploadAnswer_OfAFinalStatus_IsNoRetry()
    {
        NoRetry(new ForbiddenException("no write access", noRetry: false)).ShouldBeTrue();
        NoRetry(new ForbiddenException("no write access", noRetry: false), authenticated: false).ShouldBeTrue();
        NoRetry(new UnauthorizedException("Unauthorized", noRetry: false)).ShouldBeTrue();
        NoRetry(new NotFoundException("Dataset not found")).ShouldBeTrue();
        NoRetry(new ConflictException("Upload 'x' belongs to another user")).ShouldBeTrue();
        NoRetry(new PayloadTooLargeException("too large")).ShouldBeTrue();
        NoRetry(new QuotaExceededException(10, 20)).ShouldBeTrue();
    }

    [Test]
    public void TheRecoverableConflicts_StayRetryable()
    {
        NoRetry(new RecoverableConflictException("Upload 'x' is missing chunks: 1")).ShouldBeFalse();
        NoRetry(new RecoverableConflictException("Upload 'x' is still in progress")).ShouldBeFalse();
    }

    [Test]
    public void OtherStatuses_KeepTheClassifiersFlag()
    {
        NoRetry(new DdbBusyException("busy")).ShouldBeFalse();
        NoRetry(new InvalidOperationException("bug")).ShouldBeFalse();
        NoRetry(new BadRequestException("bad")).ShouldBeFalse();
        NoRetry(new InvalidDatasetPathException("..", EntryPathRejection.Traversal)).ShouldBeTrue();
    }

    // Outside the upload answers the classifier is unchanged: these must stay retryable there
    [Test]
    public void TheClassifier_KeepsNoRetryFalse_ForA404AndA409Elsewhere()
    {
        ApiExceptionClassifier.Classify(new NotFoundException("build pending"), true).NoRetry.ShouldBeFalse();
        ApiExceptionClassifier.Classify(new ConflictException("busy"), true).NoRetry.ShouldBeFalse();
        ApiExceptionClassifier.Classify(new UnauthorizedException("Unauthorized", noRetry: false), true).NoRetry
            .ShouldBeFalse();
    }

    [Test]
    public void PerFileResults_OfAFinalStatus_AreNotRetryable_AndATransientOneIs()
    {
        var forbidden = new UploadPartOutcome("a.jpg", null, new ForbiddenException("no write access")).ToDto(true);
        forbidden.Status.ShouldBe(StatusCodes.Status403Forbidden);
        forbidden.Retryable.ShouldBeFalse();

        var full = new UploadPartOutcome("a.jpg", null, new QuotaExceededException(10, 20)).ToDto(true);
        full.Status.ShouldBe(StatusCodes.Status507InsufficientStorage);
        full.Retryable.ShouldBeFalse();

        var busy = new UploadPartOutcome("a.jpg", null, new DdbBusyException("busy")).ToDto(true);
        busy.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        busy.Retryable.ShouldBeTrue();
    }
}
