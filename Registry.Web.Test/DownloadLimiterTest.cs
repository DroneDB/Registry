using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class DownloadLimiterTest : TestBase
{
    private ILogger<DownloadLimiter> _logger;

    [SetUp]
    public void Setup()
    {
        _logger = CreateTestLogger<DownloadLimiter>();
    }

    private DownloadLimiter CreateLimiter(int? maxConcurrent)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            MaxConcurrentDownloadsPerUser = maxConcurrent
        });
        return new DownloadLimiter(settings, _logger);
    }

    [Test]
    public void IsEnabled_NullMax_ReturnsFalse()
    {
        var limiter = CreateLimiter(null);
        limiter.IsEnabled.ShouldBeFalse();
    }

    [Test]
    public void IsEnabled_ZeroMax_ReturnsFalse()
    {
        var limiter = CreateLimiter(0);
        limiter.IsEnabled.ShouldBeFalse();
    }

    [Test]
    public void IsEnabled_PositiveMax_ReturnsTrue()
    {
        var limiter = CreateLimiter(3);
        limiter.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public void TryAcquireSlot_Disabled_AlwaysReturnsTrue()
    {
        var limiter = CreateLimiter(null);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
    }

    [Test]
    public void TryAcquireSlot_UnderLimit_ReturnsTrue()
    {
        var limiter = CreateLimiter(3);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
    }

    [Test]
    public void TryAcquireSlot_AtLimit_ReturnsFalse()
    {
        var limiter = CreateLimiter(2);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeFalse();
    }

    [Test]
    public void TryAcquireSlot_DifferentKeys_IndependentLimits()
    {
        var limiter = CreateLimiter(1);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:2").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeFalse();
        limiter.TryAcquireSlot("user:2").ShouldBeFalse();
    }

    [Test]
    public void ReleaseSlot_FreesCapacity()
    {
        var limiter = CreateLimiter(1);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeFalse();

        limiter.ReleaseSlot("user:1");

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
    }

    [Test]
    public void ReleaseSlot_Disabled_DoesNotThrow()
    {
        var limiter = CreateLimiter(null);
        Should.NotThrow(() => limiter.ReleaseSlot("user:1"));
    }

    [Test]
    public void ReleaseSlot_CleansUpZeroEntries()
    {
        var limiter = CreateLimiter(2);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.ReleaseSlot("user:1");

        // After releasing all slots, GetActiveDownloads should return 0
        limiter.GetActiveDownloads("user:1").ShouldBe(0);
    }

    [Test]
    public void ReleaseSlot_NeverGoesBelowZero()
    {
        var limiter = CreateLimiter(2);

        // Release without acquire should not go negative
        limiter.ReleaseSlot("user:1");
        limiter.ReleaseSlot("user:1");

        limiter.GetActiveDownloads("user:1").ShouldBe(0);

        // Should still be able to acquire normally
        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.GetActiveDownloads("user:1").ShouldBe(1);
    }

    [Test]
    public void GetActiveDownloads_ReturnsCorrectCount()
    {
        var limiter = CreateLimiter(5);

        limiter.GetActiveDownloads("user:1").ShouldBe(0);

        limiter.TryAcquireSlot("user:1");
        limiter.GetActiveDownloads("user:1").ShouldBe(1);

        limiter.TryAcquireSlot("user:1");
        limiter.GetActiveDownloads("user:1").ShouldBe(2);

        limiter.ReleaseSlot("user:1");
        limiter.GetActiveDownloads("user:1").ShouldBe(1);
    }

    [Test]
    public void GetActiveDownloads_UnknownKey_ReturnsZero()
    {
        var limiter = CreateLimiter(5);
        limiter.GetActiveDownloads("nonexistent").ShouldBe(0);
    }

    [Test]
    public void CanAcquireSlot_Disabled_AlwaysReturnsTrue()
    {
        var limiter = CreateLimiter(null);
        limiter.CanAcquireSlot("user:1").ShouldBeTrue();
    }

    [Test]
    public void CanAcquireSlot_UnderLimit_ReturnsTrue()
    {
        var limiter = CreateLimiter(2);

        limiter.TryAcquireSlot("user:1");
        limiter.CanAcquireSlot("user:1").ShouldBeTrue();
    }

    [Test]
    public void CanAcquireSlot_AtLimit_ReturnsFalse()
    {
        var limiter = CreateLimiter(2);

        limiter.TryAcquireSlot("user:1");
        limiter.TryAcquireSlot("user:1");
        limiter.CanAcquireSlot("user:1").ShouldBeFalse();
    }

    [Test]
    public void TryAcquireSlot_RollbackOnReject_DoesNotLeakCount()
    {
        var limiter = CreateLimiter(1);

        limiter.TryAcquireSlot("user:1").ShouldBeTrue();
        limiter.TryAcquireSlot("user:1").ShouldBeFalse();

        // The rejected attempt should not have increased the active count
        limiter.GetActiveDownloads("user:1").ShouldBe(1);
    }
}
