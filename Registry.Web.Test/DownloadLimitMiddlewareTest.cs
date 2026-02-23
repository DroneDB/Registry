using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Middlewares;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class DownloadLimitMiddlewareTest : TestBase
{
    private Mock<IDownloadLimiter> _limiterMock;
    private Mock<IAuthManager> _authManagerMock;
    private ILogger<DownloadLimitMiddleware> _logger;
    private DownloadLimitMiddleware _middleware;

    [SetUp]
    public void Setup()
    {
        _limiterMock = new Mock<IDownloadLimiter>();
        _authManagerMock = new Mock<IAuthManager>();
        _logger = CreateTestLogger<DownloadLimitMiddleware>();
        _middleware = new DownloadLimitMiddleware(_limiterMock.Object, _authManagerMock.Object, _logger);
    }

    private static DefaultHttpContext CreateContext(string path, string userId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        if (userId != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, userId)
            }, "test"));
        }

        return context;
    }

    private static RequestDelegate NextDelegate()
    {
        return _ => Task.CompletedTask;
    }

    private static RequestDelegate ThrowingDelegate()
    {
        return _ => throw new InvalidOperationException("Test exception");
    }

    #region Passthrough scenarios

    [Test]
    public async Task InvokeAsync_LimiterDisabled_PassesThrough()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(false);
        var context = CreateContext("/orgs/org1/ds/ds1/download");
        var nextCalled = false;

        // Act
        await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeTrue();
        _limiterMock.Verify(l => l.TryAcquireSlot(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InvokeAsync_NonDownloadPath_PassesThrough()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        var context = CreateContext("/orgs/org1/ds/ds1/info");
        var nextCalled = false;

        // Act
        await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeTrue();
        _limiterMock.Verify(l => l.TryAcquireSlot(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InvokeAsync_AdminUser_BypassesLimit()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(true);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "admin1");
        var nextCalled = false;

        // Act
        await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeTrue();
        _limiterMock.Verify(l => l.TryAcquireSlot(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region Path matching

    [Test]
    public async Task InvokeAsync_DownloadPath_IsIntercepted()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot(It.IsAny<string>())).Returns(true);
        var context = CreateContext("/orgs/myorg/ds/myds/download", "user1");

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        _limiterMock.Verify(l => l.TryAcquireSlot("user:user1"), Times.Once);
    }

    [Test]
    public async Task InvokeAsync_DdbPath_IsIntercepted()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot(It.IsAny<string>())).Returns(true);
        var context = CreateContext("/orgs/myorg/ds/myds/ddb", "user1");

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        _limiterMock.Verify(l => l.TryAcquireSlot("user:user1"), Times.Once);
    }

    #endregion

    #region Preflight (X-Download-Check)

    [Test]
    public async Task InvokeAsync_Preflight_SlotAvailable_Returns200()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.CanAcquireSlot("user:user1")).Returns(true);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");
        context.Request.Headers["X-Download-Check"] = "1";

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        context.Response.StatusCode.ShouldBe(200);
        _limiterMock.Verify(l => l.TryAcquireSlot(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InvokeAsync_Preflight_NoSlot_Returns429()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.CanAcquireSlot("user:user1")).Returns(false);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");
        context.Request.Headers["X-Download-Check"] = "1";

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        context.Response.StatusCode.ShouldBe(429);
    }

    #endregion

    #region Actual download limiting

    [Test]
    public async Task InvokeAsync_SlotAcquired_CallsNextAndReleases()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot("user:user1")).Returns(true);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");
        var nextCalled = false;

        // Act
        await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeTrue();
        _limiterMock.Verify(l => l.ReleaseSlot("user:user1"), Times.Once);
    }

    [Test]
    public async Task InvokeAsync_SlotRejected_Returns429_DoesNotCallNext()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot("user:user1")).Returns(false);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");
        var nextCalled = false;

        // Act
        await _middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(429);
        _limiterMock.Verify(l => l.ReleaseSlot(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InvokeAsync_NextThrows_StillReleasesSlot()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot("user:user1")).Returns(true);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _middleware.InvokeAsync(context, ThrowingDelegate()));

        _limiterMock.Verify(l => l.ReleaseSlot("user:user1"), Times.Once);
    }

    #endregion

    #region Key resolution

    [Test]
    public async Task InvokeAsync_AuthenticatedUser_UsesUserKey()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot(It.IsAny<string>())).Returns(true);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "myuser123");

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        _limiterMock.Verify(l => l.TryAcquireSlot("user:myuser123"), Times.Once);
    }

    [Test]
    public async Task InvokeAsync_AnonymousUser_UsesIpKey()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot(It.IsAny<string>())).Returns(true);
        var context = CreateContext("/orgs/org1/ds/ds1/download");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.42");

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        _limiterMock.Verify(l => l.TryAcquireSlot("ip:192.168.1.42"), Times.Once);
    }

    #endregion

    #region Response format

    [Test]
    public async Task InvokeAsync_429Response_HasRetryAfterHeader()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot(It.IsAny<string>())).Returns(false);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        context.Response.StatusCode.ShouldBe(429);
        context.Response.Headers["Retry-After"].ToString().ShouldBe("10");
    }

    [Test]
    public async Task InvokeAsync_429WithAcceptJson_ReturnsJsonContentType()
    {
        // Arrange
        _limiterMock.Setup(l => l.IsEnabled).Returns(true);
        _authManagerMock.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _limiterMock.Setup(l => l.TryAcquireSlot(It.IsAny<string>())).Returns(false);
        var context = CreateContext("/orgs/org1/ds/ds1/download", "user1");
        context.Request.Headers.Accept = "application/json";

        // Act
        await _middleware.InvokeAsync(context, NextDelegate());

        // Assert
        context.Response.ContentType.ShouldBe("application/json");
    }

    #endregion
}
