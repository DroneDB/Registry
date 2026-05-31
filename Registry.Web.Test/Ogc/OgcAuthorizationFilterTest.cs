using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
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
        _filter = new OgcAuthorizationFilter(_utils.Object, _auth.Object);
    }

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
        _utils.Setup(u => u.GetDataset("org", "ds", true, false)).Returns((Dataset?)null);
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(404);
        r.Content.ShouldContain("NotFound");
    }

    [Test]
    public async Task ReadDenied_Returns401WithAuthenticationFailed()
    {
        var ds = new Dataset { Slug = "ds" };
        _utils.Setup(u => u.GetDataset("org", "ds", true, false)).Returns(ds);
        _auth.Setup(a => a.RequestAccess(ds, AccessType.Read)).ReturnsAsync(false);
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(401);
        r.Content.ShouldContain("AuthenticationFailed");
        ctx.HttpContext.Response.Headers.ContainsKey("WWW-Authenticate").ShouldBeTrue();
    }

    [Test]
    public async Task ReadAllowed_DoesNotShortCircuit()
    {
        var ds = new Dataset { Slug = "ds" };
        _utils.Setup(u => u.GetDataset("org", "ds", true, false)).Returns(ds);
        _auth.Setup(a => a.RequestAccess(ds, AccessType.Read)).ReturnsAsync(true);
        var ctx = MakeContext("org", "ds");
        await _filter.OnAuthorizationAsync(ctx);
        ctx.Result.ShouldBeNull();
    }
}
