using Registry.Web.Test.Support;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using NUnit.Framework;
using Registry.Web.Data.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// Unit tests for OgcAuthorizationFilter. Validates the three failure paths
/// (missing slugs / unknown dataset / read denied) and the success path.
/// </summary>
[TestFixture]
public class OgcAuthorizationFilterTest
{
    private Mock<IUtils> _utils = null!;
    private Mock<IAuthManager> _auth = null!;
    private OgcAuthorizationFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _utils = new Mock<IUtils>();
        _auth = new Mock<IAuthManager>();
        _filter = new OgcAuthorizationFilter(new DatasetAccessResolver(_utils.Object, _auth.Object, Mock.Of<IDdbManager>(), TestPaths.Policy),
            NullLogger<OgcAuthorizationFilter>.Instance);
    }

    private void SetupDataset(Dataset? ds) =>
        _utils.Setup(u => u.GetDatasetAsync("org", "ds", false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ds ?? throw new System.ArgumentNullException(nameof(ds)));

    private static AuthorizationFilterContext MakeContext(string? orgSlug, string? dsSlug)
    {
        var httpContext = new DefaultHttpContext();
        var routeData = new RouteData();
        if (orgSlug != null) routeData.Values["orgSlug"] = orgSlug;
        if (dsSlug != null) routeData.Values["dsSlug"] = dsSlug;
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    [Test]
    public async Task MissingSlugs_ReturnsBadRequestWithMissingParameterValue()
    {
        var ctx = MakeContext(null, null);
        await _filter.OnAuthorizationAsync(ctx);
        ctx.Result.ShouldBeOfType<ContentResult>();
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(400);
        r.Content.ShouldContain("MissingParameterValue");
    }

    [Test]
    public async Task UnknownDataset_Returns404NotFound()
    {
        _utils.Setup(u => u.GetDatasetAsync("org", "ds", false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("Cannot find dataset ds in organization org"));
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(404);
        r.Content.ShouldContain("NotFound");
    }

    [Test]
    public async Task ReadDenied_Anonymous_Returns401WithAuthenticationFailed()
    {
        var ds = new Dataset { Slug = "ds" };
        SetupDataset(ds);
        _auth.Setup(a => a.RequestAccess(ds, AccessType.Read)).ReturnsAsync(false);
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(401);
        r.Content.ShouldContain("AuthenticationFailed");
        ctx.HttpContext.Response.Headers.ContainsKey("WWW-Authenticate").ShouldBeTrue();
    }

    [Test]
    public async Task ReadDenied_Authenticated_Returns403WithoutChallenge()
    {
        // D2 (RQ-05): signed-in OGC clients that lack access are not asked for credentials again
        var ds = new Dataset { Slug = "ds" };
        SetupDataset(ds);
        _auth.Setup(a => a.RequestAccess(ds, AccessType.Read)).ReturnsAsync(false);
        var ctx = MakeContext("org", "ds");
        ctx.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user-id")], "test"));
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(403);
        r.Content.ShouldContain("AccessDenied");
        ctx.HttpContext.Response.Headers.ContainsKey("WWW-Authenticate").ShouldBeFalse();
    }

    [Test]
    public async Task InvalidSlug_Returns400NotAServerError()
    {
        // RQ-06: the lookup's BadRequestException used to fall into the catch-all and answer 500
        _utils.Setup(u => u.GetDatasetAsync("Bad", "ds", false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BadRequestException("Invalid organization id"));
        var ctx = MakeContext("Bad", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(400);
        r.Content.ShouldContain("InvalidParameterValue");
        r.Content.ShouldContain("Invalid organization id");
    }

    [Test]
    public async Task UnexpectedFault_Returns500WithoutItsMessage()
    {
        _utils.Setup(u => u.GetDatasetAsync("org", "ds", false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.InvalidOperationException("connection string secret"));
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(500);
        r.Content.ShouldNotContain("secret");
    }

    [Test]
    public async Task ReadAllowed_DoesNotShortCircuit()
    {
        var ds = new Dataset { Slug = "ds" };
        SetupDataset(ds);
        _auth.Setup(a => a.RequestAccess(ds, AccessType.Read)).ReturnsAsync(true);
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        ctx.Result.ShouldBeNull();
    }
}
