#nullable enable
using System;
using System.Threading;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for the <see cref="SsrfHttpHandler"/> factory configuration: redirect cap and the
/// connect timeout wired from <see cref="ImportSettings.ConnectTimeoutSeconds"/>.
/// </summary>
[TestFixture]
public class SsrfHttpHandlerTests
{
    private static SsrfGuard Guard() => new(new ImportSettings());

    [Test]
    public void Create_AppliesConnectTimeoutAndRedirectCap()
    {
        using var handler = SsrfHttpHandler.Create(Guard(), maxRedirects: 5, connectTimeout: TimeSpan.FromSeconds(7));

        handler.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(7));
        handler.MaxAutomaticRedirections.ShouldBe(5);
        handler.AllowAutoRedirect.ShouldBeTrue();
    }

    [Test]
    public void Create_ClampsRedirectsToAtLeastOne()
    {
        using var handler = SsrfHttpHandler.Create(Guard(), maxRedirects: 0);
        handler.MaxAutomaticRedirections.ShouldBe(1);
    }

    [Test]
    public void Create_NullConnectTimeout_LeavesInfiniteDefault()
    {
        using var handler = SsrfHttpHandler.Create(Guard(), maxRedirects: 3, connectTimeout: null);
        handler.ConnectTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }
}
