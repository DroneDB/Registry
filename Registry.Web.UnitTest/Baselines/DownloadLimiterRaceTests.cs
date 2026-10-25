#nullable enable
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;
using Shouldly;
using Registry.Test.Common;
using Registry.Web.Test.Support;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>DownloadLimiter.ReleaseSlot</c> decremented the counter to 0 and then removed the
/// key unconditionally, so an acquire that landed between the two steps had its slot wiped (lost
/// update: the user could exceed the limit). The removal must be compare-and-remove on value 0.
/// The interleaving is reproduced deterministically through the internal
/// <c>BeforeRemoveEmptyEntry</c> hook (no threads, no sleeps).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DownloadLimiterRaceTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class DownloadLimiterRaceTests
{
    private const string Key = "user:downloadlimiterrace";

    private static DownloadLimiter CreateLimiter(int max) =>
        new(TestSettings.Options<DownloadOptions>(MsOptions.Create(new AppSettings { MaxConcurrentDownloadsPerUser = max })),
            NullLogger<DownloadLimiter>.Instance);

    [Test]
    public void ReleaseSlot_AcquireBetweenDecrementAndRemove_KeepsTheNewSlot()
    {
        var limiter = CreateLimiter(2);
        limiter.TryAcquireSlot(Key).ShouldBeTrue();

        // A concurrent request acquires a slot right after the release brought the count to 0
        limiter.BeforeRemoveEmptyEntry = key => limiter.TryAcquireSlot(key).ShouldBeTrue();

        limiter.ReleaseSlot(Key);

        limiter.GetActiveDownloads(Key).ShouldBe(1,
            "the slot acquired between the decrement and the cleanup must survive the cleanup");
    }

    [Test]
    public void ReleaseSlot_AcquireBetweenDecrementAndRemove_LimitStillEnforced()
    {
        var limiter = CreateLimiter(1);
        limiter.TryAcquireSlot(Key).ShouldBeTrue();
        limiter.BeforeRemoveEmptyEntry = key => limiter.TryAcquireSlot(key).ShouldBeTrue();

        limiter.ReleaseSlot(Key);
        limiter.BeforeRemoveEmptyEntry = null;

        // One download is active: a second one must be refused with a limit of 1
        limiter.TryAcquireSlot(Key).ShouldBeFalse();
    }

    [Test]
    public void ReleaseSlot_LastSlot_RemovesTheEntry()
    {
        var limiter = CreateLimiter(2);
        limiter.TryAcquireSlot(Key).ShouldBeTrue();

        limiter.ReleaseSlot(Key);

        limiter.GetActiveDownloads(Key).ShouldBe(0);
        limiter.CanAcquireSlot(Key).ShouldBeTrue();
    }
}
